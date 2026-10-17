using System.Security.Cryptography;

namespace MelodyBridge.Infrastructure.Jumo;

/// <summary>
/// Byte-level handling for the "secure" Qobuz stream format the Jumo
/// backend hands out: fragmented MP4 segments whose FLAC/MP3 frames are
/// AES-CTR encrypted per frame, with a custom uuid box carrying a frame
/// index (size, flags, IV) and a segment table. The layout was ported
/// from the site's own client and validated byte-for-byte against it for
/// both FLAC and MP3 output.
///
/// Init segment (segment 0): [ftyp][uuid(init)][moov...]. The init uuid
/// payload holds the native FLAC header (fLaC + STREAMINFO) for lossless
/// streams, plus a per-segment table of {byteLength, sampleCount}.
///
/// Media segments: [styp][uuid(segment)][moof][mdat]. The uuid payload
/// holds the frame index; mdat holds the frame bytes in the same order.
/// Frames with flags 0 are plain, frames with flags 1 are AES-CTR
/// encrypted with the descriptor's content key and the frame's own IV.
/// </summary>
public static class JumoContainer
{
    /// <summary>Uuid user type of the init segment box.</summary>
    private static readonly byte[] InitUserType = Convert.FromHexString("c7c75df0fdd951e98fc22971e4acf8d2");

    /// <summary>Uuid user type of the media segment box.</summary>
    private static readonly byte[] SegmentUserType = Convert.FromHexString("3b42129256f35f75923663b69a1f52b2");

    /// <summary>Parsed init segment: the output header and the segment table.</summary>
    public sealed record InitInfo(string Kind, byte[] Header, IReadOnlyList<TableEntry> Table);

    /// <summary>One row of the init segment's segment table.</summary>
    public sealed record TableEntry(uint ByteLength, uint SampleCount);

    /// <summary>One encrypted or plain frame inside a media segment.</summary>
    public sealed record FrameInfo(uint Size, ushort Flags, byte[] Iv);

    /// <summary>Frame index of one media segment plus its mdat bounds.</summary>
    public sealed record SegmentLayout(int DataStart, int MdatEnd, IReadOnlyList<FrameInfo> Frames);

    /// <summary>Decrypted output ready for the downloader to write.</summary>
    public sealed record AssembledStream(byte[] Bytes, string Container);

    private const int HeaderLength = 42; // "fLaC" + STREAMINFO block (4 + 38 marker bytes)
    private const int CtrBlockSize = 16;
    private const int CtrBlockCounterBytes = 8;

    /// <summary>
    /// Parses the init segment. Returns the native FLAC header and the
    /// segment table for lossless streams; when the payload carries no
    /// FLAC header the kind is "mp4" and the whole init segment is the
    /// output header (the client keeps the fragmented MP4 as-is).
    /// </summary>
    public static InitInfo ParseInit(ReadOnlySpan<byte> init)
    {
        var payloadRange = FindUuidPayload(init, InitUserType)
            ?? throw new InvalidDataException("Jumo init segment has no init uuid box");
        var payload = init[payloadRange.Start..payloadRange.End];
        if (payload.Length < 28)
            throw new InvalidDataException("Jumo init uuid payload too short");

        var keyLength = ReadUInt16BigEndian(payload[26..]);
        var regionLength = Math.Min(payload.Length, 28 + keyLength);
        var region = payload[..regionLength];

        var table = ParseTable(payload, keyLength);

        var flacIndex = region.IndexOf("fLaC"u8);
        if (flacIndex >= 0 && flacIndex + HeaderLength <= region.Length)
        {
            var header = region.Slice(flacIndex, HeaderLength).ToArray();
            // The stream is complete, so the STREAMINFO block is the last
            // metadata block: the site sets that bit, tag readers rely on it.
            header[4] |= 0x80;
            return new InitInfo("flac", header, table);
        }

        return new InitInfo("mp4", init.ToArray(), table);
    }

    /// <summary>Parses one media segment's frame index and mdat bounds.</summary>
    public static SegmentLayout ParseSegment(ReadOnlySpan<byte> segment)
    {
        var uuidRange = FindUuidBox(segment, SegmentUserType)
            ?? throw new InvalidDataException("Jumo media segment has no segment uuid box");
        var mdatEnd = FindBoxEnd(segment, "mdat"u8) ?? segment.Length;

        var payloadStart = uuidRange.Start + 24;
        if (payloadStart + 12 > segment.Length)
            throw new InvalidDataException("Jumo segment uuid header truncated");

        var payload = segment[payloadStart..];
        var dataOffset = ReadUInt32BigEndian(payload[4..]);
        var entrySize = payload[8];
        var frameCount = (payload[9] << 16) | (payload[10] << 8) | payload[11];
        var stride = 8 + entrySize;

        var frames = new List<FrameInfo>(frameCount);
        var entry = 12;
        for (var i = 0; i < frameCount; i++)
        {
            if (entry + stride > payload.Length)
                throw new InvalidDataException("Jumo segment uuid entries truncated");
            var size = ReadUInt32BigEndian(payload[entry..]);
            var flags = ReadUInt16BigEndian(payload[(entry + 6)..]);
            var ivLength = Math.Min((int)entrySize, 16);
            var iv = payload.Slice(entry + 8, ivLength).ToArray();
            frames.Add(new FrameInfo(size, flags, iv));
            entry += stride;
        }

        return new SegmentLayout(uuidRange.Start + (int)dataOffset, mdatEnd, frames);
    }

    /// <summary>
    /// Decrypts one AES-CTR frame. The counter block is the frame's IV in
    /// the top 8 bytes with a 64-bit big-endian block counter below it,
    /// matching WebCrypto's AES-CTR with length 64.
    /// </summary>
    public static byte[] DecryptFrame(byte[] key, byte[] iv, ReadOnlySpan<byte> ciphertext)
    {
        var counter = new byte[CtrBlockSize];
        Array.Copy(iv, counter, Math.Min(CtrBlockCounterBytes, iv.Length));

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var output = new byte[ciphertext.Length];
        var keystream = new byte[CtrBlockSize];
        for (var offset = 0; offset < ciphertext.Length; offset += CtrBlockSize)
        {
            encryptor.TransformBlock(counter, 0, CtrBlockSize, keystream, 0);
            var n = Math.Min(CtrBlockSize, ciphertext.Length - offset);
            for (var i = 0; i < n; i++)
                output[offset + i] = (byte)(ciphertext[offset + i] ^ keystream[i]);
            IncrementBlockCounter(counter);
        }
        return output;
    }

    /// <summary>
    /// Decrypts and assembles the stream: native FLAC for lossless
    /// formats, plain MP3 frames for the 320 kbps tier and the untouched
    /// fragmented MP4 when the init carried no FLAC header.
    /// </summary>
    /// <param name="effectiveFormat">Qobuz format id ("5" is MP3).</param>
    public static AssembledStream Assemble(InitInfo init, IReadOnlyList<byte[]> mediaSegments, byte[] contentKey, string effectiveFormat)
    {
        if (mediaSegments.Count != init.Table.Count)
            throw new InvalidDataException(
                $"Jumo segment mismatch: {mediaSegments.Count} audio blobs vs {init.Table.Count} table rows");

        var isMp4 = init.Kind == "mp4" && effectiveFormat != "5";
        var parts = new List<byte[]>();

        // The header (a native FLAC header, or the whole init segment for
        // the fragmented-MP4 fallback) leads every output except the MP3
        // path, where only frames are kept.
        if (isMp4 || effectiveFormat != "5")
            parts.Add(init.Header);

        for (var i = 0; i < mediaSegments.Count; i++)
        {
            var segment = mediaSegments[i];
            var layout = ParseSegment(segment);

            // The table's byte length is the segment's whole mdat payload:
            // a short read elsewhere (CDN hiccup) is caught right here
            // instead of producing a corrupt file.
            var mdatBytes = layout.MdatEnd - layout.DataStart;
            if (mdatBytes != init.Table[i].ByteLength)
                throw new InvalidDataException(
                    $"Jumo segment {i + 1} is truncated or malformed ({mdatBytes} vs {init.Table[i].ByteLength} bytes)");

            if (isMp4)
            {
                var buffer = segment.ToArray();
                DecryptInto(buffer, segment, layout, contentKey);
                parts.Add(buffer);
                continue;
            }

            var position = layout.DataStart;
            foreach (var frame in layout.Frames)
            {
                var slice = segment.AsSpan(position, (int)frame.Size);
                parts.Add(frame.Flags == 0
                    ? slice.ToArray()
                    : DecryptFrame(contentKey, frame.Iv, slice));
                position += (int)frame.Size;
            }

            if (position < layout.MdatEnd)
                parts.Add(segment.AsSpan(position, layout.MdatEnd - position).ToArray());
        }

        var container = isMp4 ? "m4a" : effectiveFormat == "5" ? "mp3" : "flac";
        return new AssembledStream(Concat(parts), container);
    }

    /// <summary>Decrypts every flagged frame in place inside a segment copy (mp4 path).</summary>
    private static void DecryptInto(byte[] buffer, ReadOnlySpan<byte> segment, SegmentLayout layout, byte[] key)
    {
        var position = layout.DataStart;
        foreach (var frame in layout.Frames)
        {
            if (frame.Flags != 0)
            {
                var plain = DecryptFrame(key, frame.Iv, segment.Slice(position, (int)frame.Size));
                Array.Copy(plain, 0, buffer, position, plain.Length);
            }
            position += (int)frame.Size;
        }
    }

    /// <summary>Parses the init payload's segment table.</summary>
    private static List<TableEntry> ParseTable(ReadOnlySpan<byte> payload, int keyLength)
    {
        var table = new List<TableEntry>();
        var position = 28 + keyLength;
        if (position + 1 > payload.Length) return table;

        var skip = payload[position];
        position += 1 + skip;
        if (position + 2 > payload.Length) return table;

        var count = ReadUInt16BigEndian(payload[position..]);
        position += 2;
        for (var i = 0; i < count && position + 8 <= payload.Length; i++)
        {
            table.Add(new TableEntry(
                ReadUInt32BigEndian(payload[position..]),
                ReadUInt32BigEndian(payload[(position + 4)..])));
            position += 8;
        }
        return table;
    }

    /// <summary>Big-endian counter increment over the low 8 bytes only.</summary>
    private static void IncrementBlockCounter(byte[] counter)
    {
        for (var i = counter.Length - 1; i >= CtrBlockCounterBytes; i--)
        {
            if (++counter[i] != 0) return;
        }
    }

    /// <summary>Iterates ISO-BMFF boxes; a size of 1 means "to the end" like the reference client.</summary>
    private static List<(string Type, int Start, int End)> Boxes(ReadOnlySpan<byte> data)
    {
        var boxes = new List<(string, int, int)>();
        var position = 0;
        while (position + 8 <= data.Length)
        {
            var size = (long)ReadUInt32BigEndian(data[position..]);
            if (size == 1) size = data.Length - position;
            if (size < 8 || position + size > data.Length) break;
            var type = System.Text.Encoding.ASCII.GetString(data.Slice(position + 4, 4));
            boxes.Add((type, position, position + (int)size));
            position += (int)size;
        }
        return boxes;
    }

    /// <summary>Finds the uuid box with the given user type; returns the box bounds.</summary>
    private static (int Start, int End)? FindUuidBox(ReadOnlySpan<byte> data, byte[] userType)
    {
        foreach (var (type, start, end) in Boxes(data))
        {
            if (type != "uuid" || start + 24 > end) continue;
            if (data.Slice(start + 8, 16).SequenceEqual(userType)) return (start, end);
        }
        return null;
    }

    /// <summary>Finds the payload range (after size, type and user type) of the uuid box.</summary>
    private static (int Start, int End)? FindUuidPayload(ReadOnlySpan<byte> data, byte[] userType)
    {
        var box = FindUuidBox(data, userType);
        return box is null ? null : (box.Value.Start + 24, box.Value.End);
    }

    /// <summary>End offset of the first box with the given type.</summary>
    private static int? FindBoxEnd(ReadOnlySpan<byte> data, ReadOnlySpan<byte> type)
    {
        foreach (var (boxType, _, end) in Boxes(data))
        {
            if (System.Text.Encoding.ASCII.GetBytes(boxType).AsSpan().SequenceEqual(type))
                return end;
        }
        return null;
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data)
        => ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];

    private static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> data)
        => (ushort)((data[0] << 8) | data[1]);

    private static byte[] Concat(List<byte[]> parts)
    {
        var total = parts.Sum(p => (long)p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }
}
