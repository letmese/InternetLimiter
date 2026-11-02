using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class QuotaAndScheduleTests
{
    [TestCase(TestTier.Tier1, "Verifies Policy.IsActive correctly evaluates time window on active days")]
    public void Policy_IsActive_EvaluatesScheduleWindow()
    {
        var rule = new ScheduleRule
        {
            App = "steam.exe",
            LimitKbps = 1000,
            StartMinute = 9 * 60,  // 09:00
            EndMinute = 17 * 60,   // 17:00
            Days = new[] { true, true, true, true, true, true, true },
            Enabled = true
        };

        // Tuesday 10:30 (inside window)
        var inside = new DateTime(2026, 10, 27, 10, 30, 0);
        TestAssert.IsTrue(Policy.IsActive(rule, inside));

        // Tuesday 19:00 (outside window)
        var outside = new DateTime(2026, 10, 27, 19, 0, 0);
        TestAssert.IsFalse(Policy.IsActive(rule, outside));
    }

    [TestCase(TestTier.Tier1, "Verifies Policy.EffectiveLimit chooses tightest active schedule cap")]
    public void Policy_EffectiveLimit_SelectsActiveCap()
    {
        var schedule = new ScheduleRule
        {
            App = "download.exe",
            LimitKbps = 500,
            StartMinute = 8 * 60,
            EndMinute = 18 * 60,
            Days = new[] { true, true, true, true, true, true, true },
            Enabled = true
        };

        var schedules = new List<ScheduleRule> { schedule };
        var atNoon = new DateTime(2026, 10, 27, 12, 0, 0);

        // Unlimited static (0) -> schedule 500 applies
        TestAssert.AreEqual(500, Policy.EffectiveLimit("download.exe", 0, schedules, atNoon));

        // Static cap 200 (tighter than 500) -> 200 applies
        TestAssert.AreEqual(200, Policy.EffectiveLimit("download.exe", 200, schedules, atNoon));

        // Static cap 1000 (looser than 500) -> 500 applies
        TestAssert.AreEqual(500, Policy.EffectiveLimit("download.exe", 1000, schedules, atNoon));

        // Another app -> unaffected by schedule
        TestAssert.AreEqual(0, Policy.EffectiveLimit("chrome.exe", 0, schedules, atNoon));
    }

    [TestCase(TestTier.Tier1, "Verifies Policy.QuotaExceeded and QuotaFraction calculations")]
    public void Policy_Quota_DetectsThresholdAndFraction()
    {
        var rule = new AppRule
        {
            App = "backup.exe",
            QuotaBytes = 100 * 1024 * 1024 // 100 MB
        };

        // Under quota: 40MB in + 10MB out = 50MB (50%)
        TestAssert.IsFalse(Policy.QuotaExceeded(rule, 40 * 1024 * 1024, 10 * 1024 * 1024));
        TestAssert.AreEqual(0.5, Policy.QuotaFraction(rule, 40 * 1024 * 1024, 10 * 1024 * 1024));

        // At quota: 70MB in + 30MB out = 100MB (100%)
        TestAssert.IsTrue(Policy.QuotaExceeded(rule, 70 * 1024 * 1024, 30 * 1024 * 1024));
        TestAssert.AreEqual(1.0, Policy.QuotaFraction(rule, 70 * 1024 * 1024, 30 * 1024 * 1024));

        // Over quota: 80MB in + 40MB out = 120MB (clamped to 1.0)
        TestAssert.IsTrue(Policy.QuotaExceeded(rule, 80 * 1024 * 1024, 40 * 1024 * 1024));
        TestAssert.AreEqual(1.0, Policy.QuotaFraction(rule, 80 * 1024 * 1024, 40 * 1024 * 1024));
    }
}
