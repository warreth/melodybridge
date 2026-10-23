using MelodyBridge.Core;

namespace MelodyBridge.Tests.Core;

/// <summary>
/// Cron's "n/step" shorthand: "0/5" means "start at 0, then every 5" like
/// every cron reader the user compares against, not "minute 0 only".
/// </summary>
[TestFixture]
public class ScanScheduleStepTests
{
    [Test]
    public void ZeroSlashFive_FiresEveryFiveMinutes_NotOnlyAtZero()
    {
        var schedule = ScanSchedule.FromCron("0/5 * * * *");
        var after = new DateTimeOffset(2026, 10, 23, 10, 2, 0, TimeSpan.Zero);

        var first = schedule.NextAfter(after);

        Assert.Multiple(() =>
        {
            Assert.That(first!.Value.Minute, Is.EqualTo(5),
                "the step starts from 0 and runs through the field, not a single 0");
            var second = schedule.NextAfter(first.Value);
            Assert.That(second!.Value.Minute, Is.EqualTo(10));
        });
    }

    [Test]
    public void StarSlashStep_Unchanged()
    {
        var schedule = ScanSchedule.FromCron("*/10 * * * *");
        var after = new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(schedule.NextAfter(after)!.Value.Minute, Is.EqualTo(10));
            Assert.That(schedule.NextAfter(after.AddMinutes(11))!.Value.Minute, Is.EqualTo(20));
        });
    }
}
