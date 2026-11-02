using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class FilterRuleEngineTests
{
    [TestCase(TestTier.Tier1, "Verifies FilterRule matches application path substring")]
    public void FilterRule_ApplicationPath_MatchesSubstring()
    {
        var rule = new FilterRule("Torrent Filter", FilterConditionType.ApplicationPath, "qbittorrent");
        TestAssert.IsTrue(rule.Matches(@"C:\Program Files\qBittorrent\qbittorrent.exe", "1.2.3.4", 80, "TCP", "", "", false));
        TestAssert.IsFalse(rule.Matches(@"C:\Program Files\Google\Chrome\chrome.exe", "1.2.3.4", 80, "TCP", "", "", false));
    }

    [TestCase(TestTier.Tier1, "Verifies FilterRule matches IP range subnet prefix")]
    public void FilterRule_IpRange_MatchesPrefix()
    {
        var rule = new FilterRule("Internal Subnet", FilterConditionType.IpRange, "10.0.0.");
        TestAssert.IsTrue(rule.Matches("", "10.0.0.15", 80, "TCP", "", "", false));
        TestAssert.IsFalse(rule.Matches("", "192.168.1.1", 80, "TCP", "", "", false));
    }

    [TestCase(TestTier.Tier1, "Verifies FilterRule matches remote destination port")]
    public void FilterRule_PortRange_MatchesPort()
    {
        var rule = new FilterRule("HTTPS Traffic", FilterConditionType.PortRange, "443");
        TestAssert.IsTrue(rule.Matches("", "1.1.1.1", 443, "TCP", "", "", false));
        TestAssert.IsFalse(rule.Matches("", "1.1.1.1", 80, "TCP", "", "", false));
    }

    [TestCase(TestTier.Tier1, "Verifies FilterRule matches transport protocol case-insensitively")]
    public void FilterRule_Protocol_MatchesCaseInsensitively()
    {
        var rule = new FilterRule("UDP Streams", FilterConditionType.Protocol, "UDP");
        TestAssert.IsTrue(rule.Matches("", "1.1.1.1", 53, "udp", "", "", false));
        TestAssert.IsTrue(rule.Matches("", "1.1.1.1", 53, "UDP", "", "", false));
        TestAssert.IsFalse(rule.Matches("", "1.1.1.1", 80, "TCP", "", "", false));
    }

    [TestCase(TestTier.Tier1, "Verifies FilterRule matches domain name with wildcards")]
    public void FilterRule_DomainName_WildcardMatches()
    {
        var rule = new FilterRule("YouTube Filter", FilterConditionType.DomainName, "*.googlevideo.com");
        TestAssert.IsTrue(rule.Matches("", "1.2.3.4", 443, "TCP", "rr1.sn-4g5ednks.googlevideo.com", "", false));
        TestAssert.IsFalse(rule.Matches("", "1.2.3.4", 443, "TCP", "www.microsoft.com", "", false));
    }

    [TestCase(TestTier.Tier1, "Verifies FilterRule matches Zone (Local vs Internet)")]
    public void FilterRule_Zone_MatchesLocalAndInternet()
    {
        var localRule = new FilterRule("Local Zone", FilterConditionType.Zone, "Local");
        TestAssert.IsTrue(localRule.Matches("", "192.168.1.1", 80, "TCP", "", "", true));
        TestAssert.IsFalse(localRule.Matches("", "8.8.8.8", 80, "TCP", "", "", false));

        var internetRule = new FilterRule("Internet Zone", FilterConditionType.Zone, "Internet");
        TestAssert.IsTrue(internetRule.Matches("", "8.8.8.8", 80, "TCP", "", "", false));
        TestAssert.IsFalse(internetRule.Matches("", "192.168.1.1", 80, "TCP", "", "", true));
    }

    [TestCase(TestTier.Tier1, "Verifies FilterRule matches application tag")]
    public void FilterRule_Tag_MatchesAppTag()
    {
        var rule = new FilterRule("Gaming Rule", FilterConditionType.Tag, "Gaming");
        TestAssert.IsTrue(rule.Matches("", "", 0, "", "", "Gaming,Streaming", false));
        TestAssert.IsFalse(rule.Matches("", "", 0, "", "", "Work,Productivity", false));
    }
}
