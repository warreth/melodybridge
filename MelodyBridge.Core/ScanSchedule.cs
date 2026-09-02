namespace MelodyBridge.Core;

/// <summary>
/// The scan schedule of a library location: manual, a fixed interval, or a
/// cron expression (which also backs the "every X at weekday HH" pickers —
/// the UI composes a cron string from them, so the evaluator stays one thing).
/// Serialized as a plain string in ScanLocationEntity.ScheduleCron.
/// </summary>
public readonly record struct ScanSchedule
{
    /// <summary>Never scans on its own; the Run scan button is the only trigger.</summary>
    public static ScanSchedule Manual { get; } = new(ScanScheduleMode.Manual, cron: null, intervalMinutes: null);

    /// <summary>
    /// The named presets every scheduling UI offers, in the same wording
    /// everywhere: manual, hourly, daily, weekly, monthly, or a custom cron
    /// expression. All presets are cron under the hood, so one evaluator
    /// decides what is due everywhere.
    /// </summary>
    public static readonly (string Label, string Cron)[] NamedPresets =
    {
        ("Hourly", "0 * * * *"),
        ("Daily", "0 3 * * *"),
        ("Weekly", "0 3 * * 1"),
        ("Monthly", "0 3 1 * *"),
    };

    /// <summary>
    /// The preset a stored schedule matches, or null for anything custom
    /// (including the interval format older rows carry). The pickers seed
    /// their select from this so an existing schedule round-trips.
    /// </summary>
    public string? NamedPreset
    {
        get
        {
            if (Mode != ScanScheduleMode.Cron) return null;
            var cron = Cron;
            foreach (var preset in NamedPresets)
                if (preset.Cron == cron) return preset.Label;
            return null;
        }
    }

    public ScanScheduleMode Mode { get; }
    /// <summary>Cron expression (5 fields: minute hour day-of-month month day-of-week) when Mode is Cron.</summary>
    public string? Cron { get; }
    /// <summary>Minutes between scans when Mode is Interval.</summary>
    public int? IntervalMinutes { get; }

    private ScanSchedule(ScanScheduleMode mode, string? cron, int? intervalMinutes)
    {
        Mode = mode;
        Cron = cron;
        IntervalMinutes = intervalMinutes;
    }

    /// <summary>A plain cron expression, e.g. "30 4 * * 1" (Mondays 04:30).</summary>
    public static ScanSchedule FromCron(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) throw new ArgumentException("Cron expression is empty.", nameof(cron));
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
            throw new ArgumentException($"'{cron}' must have exactly 5 cron fields: minute hour day-of-month month day-of-week.", nameof(cron));
        // Reject out-of-range expressions at the door: the evaluator walks
        // minute by minute, so a wrong field must never become a silent never-fire.
        if (!ValidField(fields[0], 0, 59) || !ValidField(fields[1], 0, 23)
            || !ValidField(fields[2], 1, 31) || !ValidField(fields[3], 1, 12))
            throw new ArgumentException($"'{cron}' has a field out of its cron range.", nameof(cron));
        if (fields[4] != "*" && ParseDayOfWeek(fields[4]).Count == 0)
            throw new ArgumentException($"'{cron}' has an invalid day-of-week field.", nameof(cron));
        return new ScanSchedule(ScanScheduleMode.Cron, string.Join(' ', fields), null);
    }

    /// <summary>A scan every N minutes (N &gt;= 10 so the watcher cannot busy-loop).</summary>
    public static ScanSchedule FromInterval(int minutes)
    {
        if (minutes < 10) throw new ArgumentException("Interval must be at least 10 minutes.", nameof(minutes));
        return new ScanSchedule(ScanScheduleMode.Interval, null, minutes);
    }

    /// <summary>Parse a stored string; null/empty/unknown falls back to Manual.</summary>
    public static ScanSchedule Parse(string? stored) => stored switch
    {
        null or "" => Manual,
        _ => TryParse(stored, out var s) ? s : Manual,
    };

    /// <summary>Strict parse used by the UI: bad input must surface, not silently become manual.</summary>
    public static bool TryParse(string stored, out ScanSchedule schedule)
    {
        schedule = Manual;
        if (string.IsNullOrWhiteSpace(stored)) return false;

        var value = stored.Trim();
        if (value.StartsWith("interval:", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(value["interval:".Length..], out var minutes) || minutes < 10) return false;
            schedule = FromInterval(minutes);
            return true;
        }

        // A bare cron string (validated); anything else is not ours.
        if (!value.Contains(' ')) return false;
        try
        {
            schedule = FromCron(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The stored representation for ScanLocationEntity.ScheduleCron.</summary>
    public override string ToString() => Mode switch
    {
        ScanScheduleMode.Interval => $"interval:{IntervalMinutes}",
        ScanScheduleMode.Cron => Cron!,
        _ => "",
    };

    /// <summary>
    /// Short human text for cards and rows: the preset name, "every N
    /// minutes", "cron {expression}", or "manual". One wording everywhere.
    /// </summary>
    public string Describe() => Mode switch
    {
        ScanScheduleMode.Manual => "manual",
        ScanScheduleMode.Interval => $"every {IntervalMinutes} min",
        _ => NamedPreset is { } preset ? preset.ToLowerInvariant() : $"cron {Cron}",
    };

    /// <summary>
    /// True when the location is due for a scan: at or past the next time the
    /// schedule fires after <paramref name="lastScan"/>. Manual is never due;
    /// a null lastScan is always due (first run after the schedule was added).
    /// </summary>
    public bool IsDue(DateTimeOffset lastScan, DateTimeOffset now) => Mode switch
    {
        ScanScheduleMode.Manual => false,
        ScanScheduleMode.Interval => now >= lastScan.AddMinutes(IntervalMinutes!.Value),
        // (MaxValue sentinel from NextAfter is larger than any real now, so an
        // impossible expression like "0 3 30 2 *" is simply never due.)
        ScanScheduleMode.Cron => NextAfter(lastScan, cron: Cron!) <= now,
        _ => false,
    };

    /// <summary>Next fire time strictly after <paramref name="after"/>; null when the schedule never fires again.</summary>
    public DateTimeOffset? NextAfter(DateTimeOffset after)
        => Mode == ScanScheduleMode.Cron ? NextAfter(after, Cron!) : null;

    private static DateTimeOffset NextAfter(DateTimeOffset after, string cron)
    {
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var minute = ParseField(fields[0], 0, 59);
        var hour = ParseField(fields[1], 0, 23);
        var dayOfMonth = ParseField(fields[2], 1, 31);
        var month = ParseField(fields[3], 1, 12);
        var dayOfWeek = ParseDayOfWeek(fields[4]);

        // Walk forward minute by minute, bounded: a valid cron with any fire
        // time within 366 days is found long before the cap; the cap keeps a
        // pathological expression (e.g. "0 0 30 2 *") from looping forever.
        // Start at the next whole minute after 'after'.
        var t = after.UtcDateTime.AddMinutes(1).AddSeconds(-after.UtcDateTime.Second).AddMilliseconds(-after.UtcDateTime.Millisecond);
        var limit = t.AddYears(1);
        for (; t <= limit; t = t.AddMinutes(1))
        {
            if (!minute.Contains(t.Minute)) continue;
            if (!hour.Contains(t.Hour)) continue;
            if (!month.Contains(t.Month)) continue;
            var domOk = dayOfMonth.Contains(t.Day);
            // Cron rule: when both day-of-month and day-of-week are restricted, either may match.
            var dowRestricted = fields[4] != "*";
            var domRestricted = fields[2] != "*";
            var dowOk = dayOfWeek.Contains((int)t.DayOfWeek);
            if (domRestricted && dowRestricted)
            {
                if (!domOk && !dowOk) continue;
            }
            else if (!domOk || !dowOk) continue;
            return new DateTimeOffset(t, TimeSpan.Zero);
        }
        return DateTimeOffset.MaxValue; // sentinel: never fires. IsDue compares <= now, so this stays not-due.
    }

    private static bool ValidField(string field, int min, int max)
    {
        var values = ParseField(field, min, max);
        return values.Count > 0; // an out-of-range or malformed part simply drops out; empty means invalid
    }

    private static HashSet<int> ParseField(string field, int min, int max)
    {
        var values = new HashSet<int>();
        foreach (var part in field.Split(','))
            if (TryParsePart(part, min, max, out var v)) values.UnionWith(v);
        return values;
    }

    private static bool TryParsePart(string part, int min, int max, out IEnumerable<int> values)
    {
        values = Enumerable.Empty<int>();
        var (rangePart, step) = (part, 1);
        var slash = part.IndexOf('/');
        if (slash >= 0)
        {
            if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0) return false;
            rangePart = part[..slash];
        }

        int from, to;
        if (rangePart == "*") { from = min; to = max; }
        else if (rangePart.Contains('-'))
        {
            var bits = rangePart.Split('-');
            if (bits.Length != 2 || !int.TryParse(bits[0], out from) || !int.TryParse(bits[1], out to)) return false;
        }
        else if (!int.TryParse(rangePart, out from)) return false;
        else to = from;

        if (from < min || to > max || from > to) return false;
        values = Enumerable.Range(from, to - from + 1).Where(i => (i - from) % step == 0);
        return true;
    }

    // Day-of-week: cron numbering 0=Sunday (7 also Sunday), DateTime uses 0=Sunday.
    private static HashSet<int> ParseDayOfWeek(string field)
    {
        if (field == "*") return new HashSet<int>(Enumerable.Range(0, 7));
        var values = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            var p = part;
            if (p.Length == 2 && p.EndsWith('7') && int.TryParse(p[..1], out _)) p = p[..1] + "0"; // 7 -> Sunday(0)
            var bits = p.Split('-');
            if (bits.Length == 2 && int.TryParse(bits[0], out var lo) && int.TryParse(bits[1], out var hi))
            {
                if (lo < 0 || lo > 7 || hi < 0 || hi > 7 || lo > hi) continue;
                for (var d = lo; d <= hi; d++) values.Add(d == 7 ? 0 : d);
            }
            else if (int.TryParse(p, out var single) && single >= 0 && single <= 7)
                values.Add(single == 7 ? 0 : single);
        }
        return values;
    }
}

public enum ScanScheduleMode
{
    Manual,
    Interval,
    Cron,
}
