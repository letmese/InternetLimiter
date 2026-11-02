using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier2_BoundaryCornerCases;

public sealed class FilterRuleEdgeCasesTests
{
    [TestCase(TestTier.Tier2, "Verifies FilterRule handles empty and null inputs safely without throwing")]
    public void FilterRule_EmptyAndNullInputs_ReturnsFalseSafely()
    {
        var appRule = new FilterRule("Empty App", FilterConditionType.ApplicationPath, "app");
        TestAssert.IsFalse(appRule.Matches("", "1.1.1.1", 80, "TCP", "", "", false));

        var ipRule = new FilterRule("Empty IP", FilterConditionType.IpRange, "10.0.");
        TestAssert.IsFalse(ipRule.Matches(@"C:\test.exe", "", 80, "TCP", "", "", false));

        var domainRule = new FilterRule("Empty Domain", FilterConditionType.DomainName, "*.com");
        TestAssert.IsFalse(domainRule.Matches(@"C:\test.exe", "1.1.1.1", 80, "TCP", "", "", false));

        var tagRule = new FilterRule("Empty Tag", FilterConditionType.Tag, "Gaming");
        TestAssert.IsFalse(tagRule.Matches(@"C:\test.exe", "1.1.1.1", 80, "TCP", "", "", false));
    }

    [TestCase(TestTier.Tier2, "Verifies PortRange matching returns false for non-numeric port strings")]
    public void FilterRule_InvalidPortStrings_ReturnsFalse()
    {
        var rule = new FilterRule("Invalid Port", FilterConditionType.PortRange, "not_a_port");
        TestAssert.IsFalse(rule.Matches("", "1.1.1.1", 80, "TCP", "", "", false));

        // Out of range port (> 65535)
        var outOfRange = new FilterRule("Port Out Of Range", FilterConditionType.PortRange, "70000");
        TestAssert.IsFalse(outOfRange.Matches("", "1.1.1.1", 80, "TCP", "", "", false));
    }

    [TestCase(TestTier.Tier2, "Verifies DomainName matching with wildcard variations")]
    public void FilterRule_DomainWildcard_EdgeVariations()
    {
        // Wildcard starting with *.
        var rule1 = new FilterRule("Wildcard", FilterConditionType.DomainName, "*.youtube.com");
        TestAssert.IsTrue(rule1.Matches("", "", 0, "", "i.ytimg.youtube.com", "", false));
        TestAssert.IsTrue(rule1.Matches("", "", 0, "", "youtube.com", "", false));
        TestAssert.IsFalse(rule1.Matches("", "", 0, "", "vimeo.com", "", false));

        // Substring domain search (no wildcard)
        var rule2 = new FilterRule("Substring", FilterConditionType.DomainName, "netflix");
        TestAssert.IsTrue(rule2.Matches("", "", 0, "", "assets.netflix.com", "", false));
        TestAssert.IsFalse(rule2.Matches("", "", 0, "", "amazon.com", "", false));
    }
}
