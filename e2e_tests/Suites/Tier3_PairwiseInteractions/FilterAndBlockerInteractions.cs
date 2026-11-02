using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier3_PairwiseInteractions;

public sealed class FilterAndBlockerInteractions
{
    [TestCase(TestTier.Tier3, "Verifies FilterRule with Blocked=true applies blocking action to matching traffic flows")]
    public void FilterRule_WithBlockFlag_DeterminesFlowBlockAction()
    {
        var blockP2P = new FilterRule("Block BitTorrent Port", FilterConditionType.PortRange, "6881")
        {
            Blocked = true
        };

        var allowWeb = new FilterRule("Allow HTTPS", FilterConditionType.PortRange, "443")
        {
            Blocked = false
        };

        var filters = new List<FilterRule> { blockP2P, allowWeb };

        // Test Flow 1: P2P traffic to port 6881 -> should match block rule
        var matchingFilter1 = filters.FirstOrDefault(f => f.Matches(@"C:\torrent.exe", "1.2.3.4", 6881, "TCP", "", "", false));
        TestAssert.IsNotNull(matchingFilter1);
        TestAssert.IsTrue(matchingFilter1!.Blocked);

        // Test Flow 2: Web traffic to port 443 -> should match allow rule
        var matchingFilter2 = filters.FirstOrDefault(f => f.Matches(@"C:\browser.exe", "1.1.1.1", 443, "TCP", "", "", false));
        TestAssert.IsNotNull(matchingFilter2);
        TestAssert.IsFalse(matchingFilter2!.Blocked);
    }

    [TestCase(TestTier.Tier3, "Verifies BlockerState interactive cycle integrates with ProcessRow block status")]
    public void BlockerState_ProcessRowIntegration_TogglesState()
    {
        var row = new ProcessRow(8888, "client.exe", @"C:\App\client.exe");
        TestAssert.IsFalse(row.IsBlocked);

        // Change blocker decision to Deny
        var state = BlockerState.NotSet.Next(); // Deny
        row.IsBlocked = (state == BlockerState.Deny);
        TestAssert.IsTrue(row.IsBlocked);

        // Change blocker decision to Allow
        state = state.Next(); // Allow
        row.IsBlocked = (state == BlockerState.Deny);
        TestAssert.IsFalse(row.IsBlocked);
    }
}
