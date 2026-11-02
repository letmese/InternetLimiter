using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier3_PairwiseInteractions;

public sealed class RateAndPriorityInteractions
{
    [TestCase(TestTier.Tier3, "Verifies ProcessRow correctly integrates simultaneous bandwidth limits and traffic priority")]
    public void ProcessRow_RateAndPriorityCoexistence_ReflectsInProperties()
    {
        var row = new ProcessRow(3344, "streamer.exe", @"C:\OBS\obs64.exe")
        {
            LimitKbps = 5000,
            DownloadLimitKbps = 10000,
            Priority = TrafficPriority.High
        };

        TestAssert.IsTrue(row.HasLimit);
        TestAssert.AreEqual("5000 kbit/s", row.LimitText);
        TestAssert.AreEqual("10000 kbit/s", row.DownloadLimitText);
        TestAssert.AreEqual("high", row.PriorityText);
        TestAssert.AreEqual(TrafficPriority.High, row.Priority);
    }

    [TestCase(TestTier.Tier3, "Verifies TreeNode hierarchical projection reflects child process limits and priority correctly")]
    public void TreeNode_HierarchicalProjection_ReflectsProcessLimitsAndPriority()
    {
        var process = new ProcessRow(5566, "game.exe", @"C:\Steam\game.exe")
        {
            LimitKbps = 1500,
            Priority = TrafficPriority.High,
            IsBlocked = false
        };

        var node = new TreeNode(1, "app:game.exe", "game.exe", process.Path, process);
        node.RefreshDerived();

        TestAssert.IsTrue(node.HasLimit);
        TestAssert.IsTrue(node.HasUploadLimit);
        TestAssert.AreEqual(1500, node.LimitKbps);
        TestAssert.AreEqual(TrafficPriority.High, node.Priority);
        TestAssert.AreEqual("high", node.PriorityText);
        TestAssert.IsFalse(node.Blocked);
    }
}
