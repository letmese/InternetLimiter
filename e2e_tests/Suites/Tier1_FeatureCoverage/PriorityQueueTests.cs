using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class PriorityQueueTests
{
    [TestCase(TestTier.Tier1, "Verifies TrafficPriority enum values and mappings")]
    public void TrafficPriority_EnumValues_ContainExpectedLevels()
    {
        TestAssert.AreEqual(0, (int)TrafficPriority.Low);
        TestAssert.AreEqual(1, (int)TrafficPriority.Normal);
        TestAssert.AreEqual(2, (int)TrafficPriority.High);
    }

    [TestCase(TestTier.Tier1, "Verifies ProcessRow priority property and PriorityText formatting")]
    public void ProcessRow_PriorityText_MapsCorrectly()
    {
        var row = new ProcessRow(100, "game.exe", @"C:\Games\game.exe");
        TestAssert.AreEqual(TrafficPriority.Normal, row.Priority);
        TestAssert.AreEqual("normal", row.PriorityText);

        row.Priority = TrafficPriority.High;
        TestAssert.AreEqual("high", row.PriorityText);

        row.Priority = TrafficPriority.Low;
        TestAssert.AreEqual("low", row.PriorityText);
    }

    [TestCase(TestTier.Tier1, "Verifies FilterRule supports Priority configuration")]
    public void FilterRule_PriorityAssignment_RetainsValue()
    {
        var rule = new FilterRule("VoIP Priority", FilterConditionType.PortRange, "5060")
        {
            Priority = TrafficPriority.High
        };

        TestAssert.AreEqual(TrafficPriority.High, rule.Priority);
    }
}
