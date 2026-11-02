using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class ConnectionBlockingTests
{
    [TestCase(TestTier.Tier1, "Verifies BlockerState cycle: NotSet -> Deny -> Allow -> Ask -> NotSet")]
    public void BlockerState_Next_CyclesThroughStates()
    {
        var s0 = BlockerState.NotSet;
        var s1 = s0.Next();
        var s2 = s1.Next();
        var s3 = s2.Next();
        var s4 = s3.Next();

        TestAssert.AreEqual(BlockerState.Deny, s1);
        TestAssert.AreEqual(BlockerState.Allow, s2);
        TestAssert.AreEqual(BlockerState.Ask, s3);
        TestAssert.AreEqual(BlockerState.NotSet, s4);
    }

    [TestCase(TestTier.Tier1, "Verifies BlockerState Label formatting")]
    public void BlockerState_Labels_MatchSpecification()
    {
        TestAssert.AreEqual("Not set", BlockerState.NotSet.Label());
        TestAssert.AreEqual("Deny", BlockerState.Deny.Label());
        TestAssert.AreEqual("Allow", BlockerState.Allow.Label());
    }

    [TestCase(TestTier.Tier1, "Verifies FirewallRule initialization, default properties and 32-char GUID")]
    public void FirewallRule_Defaults_AreValid()
    {
        var rule = new FirewallRule
        {
            AppPath = @"C:\Windows\System32\cmd.exe",
            Label = "Block Command Prompt",
            Block = true,
            Enabled = true
        };

        TestAssert.IsNotNull(rule.Id);
        TestAssert.AreEqual(32, rule.Id.Length);
        TestAssert.IsTrue(rule.Block);
        TestAssert.IsTrue(rule.Enabled);
        TestAssert.AreEqual(@"C:\Windows\System32\cmd.exe", rule.AppPath);
    }

    [TestCase(TestTier.Tier1, "Verifies WfpFirewall engine instantiates safely reporting availability")]
    public void WfpFirewall_Initialization_DoesNotCrash()
    {
        using var fw = new WfpFirewall();
        // Regardless of admin elevation, it must instantiate without unhandled crash
        TestAssert.IsNotNull(fw);
        if (!fw.IsAvailable)
        {
            TestAssert.IsNotNull(fw.UnavailableReason);
        }
    }
}
