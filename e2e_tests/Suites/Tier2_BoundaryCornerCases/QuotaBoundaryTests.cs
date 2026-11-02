using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier2_BoundaryCornerCases;

public sealed class QuotaBoundaryTests
{
    [TestCase(TestTier.Tier2, "Verifies QuotaExceeded triggers at exact byte equality (usedIn + usedOut == QuotaBytes)")]
    public void Quota_ExactByteEquality_TriggersExceeded()
    {
        var rule = new AppRule { QuotaBytes = 5000 };

        // 4999 bytes: Not exceeded
        TestAssert.IsFalse(Policy.QuotaExceeded(rule, 2500, 2499));

        // 5000 bytes: Exactly exceeded
        TestAssert.IsTrue(Policy.QuotaExceeded(rule, 2500, 2500));
    }

    [TestCase(TestTier.Tier2, "Verifies rule with QuotaBytes = 0 is treated as unlimited")]
    public void Quota_ZeroQuota_NeverExceeded()
    {
        var rule = new AppRule { QuotaBytes = 0 };

        TestAssert.IsFalse(Policy.QuotaExceeded(rule, 1_000_000_000, 1_000_000_000));
        TestAssert.AreEqual(0.0, Policy.QuotaFraction(rule, 1_000_000_000, 1_000_000_000));
    }

    [TestCase(TestTier.Tier2, "Verifies QuotaFraction is strictly clamped to [0.0, 1.0]")]
    public void Quota_FractionClamping_BoundsCorrectly()
    {
        var rule = new AppRule { QuotaBytes = 1000 };

        // 0%
        TestAssert.AreEqual(0.0, Policy.QuotaFraction(rule, 0, 0));

        // 200% usage -> clamped to 1.0
        TestAssert.AreEqual(1.0, Policy.QuotaFraction(rule, 1500, 500));
    }

    [TestCase(TestTier.Tier2, "Verifies Quota arithmetic operates safely on 64-bit Terabyte scales without overflow")]
    public void Quota_LargeByteCounters_64BitScale_NoOverflow()
    {
        // 50 Terabytes = 50 * 1024^4 bytes
        long fiftyTb = 50L * 1024 * 1024 * 1024 * 1024;
        var rule = new AppRule { QuotaBytes = fiftyTb };

        long usedIn = 30L * 1024 * 1024 * 1024 * 1024;
        long usedOut = 20L * 1024 * 1024 * 1024 * 1024;

        TestAssert.IsTrue(Policy.QuotaExceeded(rule, usedIn, usedOut));
        TestAssert.AreEqual(1.0, Policy.QuotaFraction(rule, usedIn, usedOut));
    }
}
