using System.Text;

namespace MelodyBridge.Tests;

/// <summary>
/// A POSIX shell stub for the CLI-backed plugin tests (deemix,
/// qobuz-dlp): records its arguments next to the script and writes a
/// small sniffable FLAC into the folder the tool was told to use, so the
/// whole plugin path runs without the real tool. Windows skips (no
/// shell), which the tests declare.
/// </summary>
public static class CliStub
{
    /// <summary>Writes an executable script that produces an audio file.</summary>
    public static string Producing(
        string directory,
        string audioName = "Artist - Song.flac",
        string name = "cli.sh",
        int exitCode = 0,
        string stderr = "")
    {
        var body = new StringBuilder();
        body.AppendLine("echo \"$@\" > \"$(dirname \"$0\")/args.txt\"");
        body.AppendLine("dir=\"\"");
        body.AppendLine("prev=\"\"");
        body.AppendLine("for a in \"$@\"; do");
        body.AppendLine("  case \"$prev\" in");
        body.AppendLine("    -p|--directory) dir=\"$a\";;");
        body.AppendLine("  esac");
        body.AppendLine("  prev=\"$a\"");
        body.AppendLine("done");
        if (exitCode != 0)
        {
            if (stderr.Length > 0)
                body.AppendLine($"echo \"{stderr}\" 1>&2");
            body.AppendLine($"exit {exitCode}");
        }
        else
        {
            body.AppendLine("if [ -z \"$dir\" ]; then echo 'no directory argument' 1>&2; exit 2; fi");
            body.AppendLine("mkdir -p \"$dir\"");
            body.AppendLine($"printf 'fLaC' > \"$dir/{audioName}\"");
            body.AppendLine($"head -c 200 /dev/zero >> \"$dir/{audioName}\"");
            body.AppendLine("exit 0");
        }
        return Write(directory, name, body.ToString());
    }

    /// <summary>Writes an executable script that exits successfully but produces nothing.</summary>
    public static string Silent(string directory, string name = "silent.sh")
        => Write(directory, name, "echo \"$@\" > \"$(dirname \"$0\")/args.txt\"\nexit 0\n");

    /// <summary>The recorded arguments of the last run, or "".</summary>
    public static string RecordedArguments(string scriptPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(scriptPath)!, "args.txt");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private static string Write(string directory, string name, string body)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }
}
