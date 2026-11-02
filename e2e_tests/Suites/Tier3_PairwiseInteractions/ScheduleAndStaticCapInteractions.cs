using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier3_PairwiseInteractions;

public sealed class ScheduleAndStaticCapInteractions
{
    [TestCase(TestTier.Tier3, "Verifies multiple overlapping active schedules select the tightest limit")]
    public void EffectiveLimit_MultipleOverlappingSchedules_TighterWins()
    {
        var generalDaySchedule = new ScheduleRule
        {
            App = "torrent.exe",
            LimitKbps = 1000,
            StartMinute = 8 * 60,
            EndMinute = 20 * 60,
            Days = new[] { true, true, true, true, true, true, true },
            Enabled = true
        };

        var peakHoursSchedule = new ScheduleRule
        {
            App = "torrent.exe",
            LimitKbps = 250, // Much tighter cap during peak hours
            StartMinute = 12 * 60,
            EndMinute = 16 * 60,
            Days = new[] { true, true, true, true, true, true, true },
            Enabled = true
        };

        var schedules = new List<ScheduleRule> { generalDaySchedule, peakHoursSchedule };

        // 14:00 (Both active) -> 250 Kbps must win
        var peakTime = new DateTime(2026, 10, 27, 14, 0, 0);
        TestAssert.AreEqual(250, Policy.EffectiveLimit("torrent.exe", 0, schedules, peakTime));

        // 10:00 (Only general active) -> 1000 Kbps must win
        var morningTime = new DateTime(2026, 10, 27, 10, 0, 0);
        TestAssert.AreEqual(1000, Policy.EffectiveLimit("torrent.exe", 0, schedules, morningTime));

        // 22:00 (Neither active) -> static cap (0) applies
        var nightTime = new DateTime(2026, 10, 27, 22, 0, 0);
        TestAssert.AreEqual(0, Policy.EffectiveLimit("torrent.exe", 0, schedules, nightTime));
    }

    [TestCase(TestTier.Tier3, "Verifies tight static cap overrides looser active schedule")]
    public void EffectiveLimit_TightStaticCap_OverridesLooserSchedule()
    {
        var schedule = new ScheduleRule
        {
            App = "zoom.exe",
            LimitKbps = 2000,
            StartMinute = 8 * 60,
            EndMinute = 18 * 60,
            Days = new[] { true, true, true, true, true, true, true },
            Enabled = true
        };

        var schedules = new List<ScheduleRule> { schedule };
        var atNoon = new DateTime(2026, 10, 27, 12, 0, 0);

        // Static cap is 800 (tighter than 2000) -> 800 must apply
        int effective = Policy.EffectiveLimit("zoom.exe", 800, schedules, atNoon);
        TestAssert.AreEqual(800, effective);
    }
}
