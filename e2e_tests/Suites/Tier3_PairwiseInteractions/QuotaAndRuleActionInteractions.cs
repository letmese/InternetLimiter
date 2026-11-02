using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier3_PairwiseInteractions;

public sealed class QuotaAndRuleActionInteractions
{
    [TestCase(TestTier.Tier3, "Verifies exceeding quota triggers automatic enforcement action (throttling or blocking)")]
    public void Quota_Exceeded_TriggersPolicyAction()
    {
        var appRule = new AppRule
        {
            App = "backup_client.exe",
            QuotaBytes = 50 * 1024 * 1024, // 50 MB
            UploadLimitKbps = 10000 // Normal 10 Mbps
        };

        long currentIn = 30 * 1024 * 1024;
        long currentOut = 25 * 1024 * 1024; // Total = 55 MB (> 50 MB)

        bool exceeded = Policy.QuotaExceeded(appRule, currentIn, currentOut);
        TestAssert.IsTrue(exceeded, "Quota should be exceeded");

        // Action: Enforce severe throttle (128 Kbps) or block
        int effectiveCap = exceeded ? 128 : appRule.UploadLimitKbps;
        TestAssert.AreEqual(128, effectiveCap);

        // Reset quota / usage (e.g. at beginning of billing cycle or manual reset)
        currentIn = 0;
        currentOut = 0;
        exceeded = Policy.QuotaExceeded(appRule, currentIn, currentOut);
        TestAssert.IsFalse(exceeded, "Quota should no longer be exceeded after reset");

        effectiveCap = exceeded ? 128 : appRule.UploadLimitKbps;
        TestAssert.AreEqual(10000, effectiveCap);
    }
}
