using MelodyBridge.Core;

namespace MelodyBridge.Tests.Core;

/// <summary>
/// ScanSchedule evaluation on real cron semantics: no fakes, just the
/// pure model. Every assertion pins a fire time a user would actually pick.
/// </summary>
[TestFixture]
public class ScanScheduleTests
{
    // 2026-07-06 is a Monday.
    private static readonly DateTimeOffset Monday = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void Manual_NeverDue()
    {
        var s = ScanSchedule.Manual;
        Assert.That(s.IsDue(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), Is.False);
        Assert.That(s.ToString(), Is.EqualTo(""));
    }

    [Test]
    public void Interval_DueExactlyAtInterval()
    {
        var s = ScanSchedule.FromInterval(30);
        var last = Monday;

        Assert.That(s.IsDue(last, last.AddMinutes(29)), Is.False, "one minute early must not be due");
        Assert.That(s.IsDue(last, last.AddMinutes(30)), Is.True, "exactly at the interval must be due");
        Assert.That(s.NextAfter(last), Is.Null, "interval mode has no cron next-time; it is a plain offset");
    }

    [Test]
    public void Interval_RoundTripsThroughString()
    {
        var s = ScanSchedule.FromInterval(45);
        Assert.That(ScanSchedule.Parse(s.ToString()).IntervalMinutes, Is.EqualTo(45));
        Assert.That(ScanSchedule.Parse("interval:120").IntervalMinutes, Is.EqualTo(120));
    }

    [Test]
    public void Interval_RejectsValuesBelowMinimum()
    {
        Assert.That(() => ScanSchedule.FromInterval(5), Throws.ArgumentException,
            "intervals under 10 minutes would busy-loop the scheduler");
        Assert.That(ScanSchedule.TryParse("interval:5", out _), Is.False);
    }

    [TestCase("30 4 * * *", 4, 30)]              // daily at 04:30
    [TestCase("0 18 * * 5", 18, 0)]              // Fridays 18:00
    [TestCase("15 3 1 * *", 3, 15)]              // 1st of the month 03:15
    public void Cron_FiresAtThePickedTime(string cron, int hour, int minute)
    {
        var s = ScanSchedule.FromCron(cron);
        var next = s.NextAfter(Monday);
        Assert.That(next, Is.Not.Null, $"'{cron}' must have a next fire time");
        Assert.That(next!.Value.Hour, Is.EqualTo(hour));
        Assert.That(next.Value.Minute, Is.EqualTo(minute));
        Assert.That(next.Value > Monday, Is.True, "next fire time is strictly after the reference");
    }

    [Test]
    public void Cron_MondayPick_FiresNextMonday()
    {
        // 2026-07-06 Monday 12:00 -> weekly cron at 09:00 on Mondays (1) fires 2026-07-13 09:00.
        var s = ScanSchedule.FromCron("0 9 * * 1");
        var next = s.NextAfter(Monday);
        Assert.That(next!.Value, Is.EqualTo(new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public void Cron_SameDayLaterHour_FiresToday()
    {
        // Monday 12:00, cron 22:00 daily -> still fires the same Monday.
        var s = ScanSchedule.FromCron("0 22 * * *");
        var next = s.NextAfter(Monday);
        Assert.That(next!.Value, Is.EqualTo(new DateTimeOffset(2026, 7, 6, 22, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public void Cron_SundayIsZeroOrSeven()
    {
        var s = ScanSchedule.FromCron("0 8 * * 0");
        Assert.That(s.NextAfter(Monday)!.Value, Is.EqualTo(new DateTimeOffset(2026, 7, 12, 8, 0, 0, TimeSpan.Zero)));

        var s7 = ScanSchedule.FromCron("0 8 * * 7"); // 7 is also Sunday
        Assert.That(s7.NextAfter(Monday)!.Value, Is.EqualTo(new DateTimeOffset(2026, 7, 12, 8, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public void Cron_IsDue_RespectsLastScan()
    {
        var s = ScanSchedule.FromCron("0 9 * * 1");
        var lastScan = new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero); // scanned at Monday 09:00

        Assert.That(s.IsDue(lastScan, new DateTimeOffset(2026, 7, 13, 8, 59, 0, TimeSpan.Zero)), Is.False);
        Assert.That(s.IsDue(lastScan, new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero)), Is.True);
    }

    [Test]
    public void Cron_RoundTripsAndParsesBareExpression()
    {
        var s = ScanSchedule.FromCron("30 4 * * *");
        Assert.That(s.ToString(), Is.EqualTo("30 4 * * *"));
        var parsed = ScanSchedule.Parse("30 4 * * *");
        Assert.That(parsed.Mode, Is.EqualTo(ScanScheduleMode.Cron));
        Assert.That(parsed.Cron, Is.EqualTo("30 4 * * *"));
    }

    [Test]
    public void Parse_EmptyOrNullIsManual()
        => Assert.That(ScanSchedule.Parse(null).Mode, Is.EqualTo(ScanScheduleMode.Manual));

    [Test]
    public void Parse_LegacyIntervalHours_StillWorks()
    {
        // Sanity that a plain number never parses as a schedule: stored values
        // are only "" / "interval:N" / cron strings.
        Assert.That(ScanSchedule.TryParse("24", out _), Is.False);
        Assert.That(ScanSchedule.Parse("24").Mode, Is.EqualTo(ScanScheduleMode.Manual),
            "unknown shapes degrade to manual, never crash the background loop");
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("0 4 * *")]           // 4 fields
    [TestCase("0 25 * * *")]        // hour out of range
    [TestCase("70 * * * *")]       // minute out of range
    [TestCase("* * * * 8")]         // day-of-week out of range
    public void InvalidCron_IsRejected(string bad)
    {
        Assert.That(() => ScanSchedule.FromCron(bad), Throws.ArgumentException);
        Assert.That(ScanSchedule.TryParse(bad, out _), Is.False,
            "bad input must not round-trip through TryParse either");
    }

    [Test]
    public void Cron_StepAndListFields_FireCorrectly()
    {
        // Every 15 minutes of the 4th hour: minute field "*/15" -> 0,15,30,45.
        var s = ScanSchedule.FromCron("*/15 4 * * *");
        var next = s.NextAfter(Monday); // Monday 12:00 -> Tuesday 04:00
        Assert.That(next!.Value, Is.EqualTo(new DateTimeOffset(2026, 7, 7, 4, 0, 0, TimeSpan.Zero)));

        // List: "10,40 6 * * *" fires at 06:10 and 06:40.
        var list = ScanSchedule.FromCron("10,40 6 * * *");
        var afterFirst = new DateTimeOffset(2026, 7, 7, 6, 10, 0, TimeSpan.Zero);
        Assert.That(list.NextAfter(afterFirst)!.Value, Is.EqualTo(new DateTimeOffset(2026, 7, 7, 6, 40, 0, TimeSpan.Zero)));
    }

    [Test]
    public void Cron_ImpossibleDayOfMonth_NeverDue()
    {
        // Day 30 in February never exists: the bounded walk finds nothing and
        // the MaxValue sentinel keeps it not-due against any realistic now.
        var s = ScanSchedule.FromCron("0 3 30 2 *");
        var next = s.NextAfter(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.That(next, Is.EqualTo(DateTimeOffset.MaxValue),
            "an impossible expression must produce the never-fires sentinel");
        Assert.That(s.IsDue(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)), Is.False);
    }
}
