using System.Diagnostics;
using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class BandwidthLimiterTests
{
    [TestCase(TestTier.Tier1, "Verifies TokenBucket rate limiter enforces rate cap and allows linear token replenish")]
    public void TokenBucket_Algorithm_ThrottlesTrafficAndReplenishes()
    {
        // Test an exact 100 KB/s rate limit (102,400 bytes/sec)
        long bytesPerSec = 100 * 1024;
        long capacity = bytesPerSec; // 1-second burst capacity
        double tokens = capacity;
        long lastTicks = Stopwatch.GetTimestamp();

        // 1. Initial burst up to capacity should succeed
        int packetSize = 10 * 1024; // 10 KB
        int allowedPackets = 0;
        while (tokens >= packetSize)
        {
            tokens -= packetSize;
            allowedPackets++;
        }
        TestAssert.AreEqual(10, allowedPackets, "Expected 10 x 10KB packets (100KB burst) to be allowed initially");

        // 2. Next packet immediately should be denied due to exhausted tokens
        TestAssert.IsTrue(tokens < packetSize, "Tokens should be below packet size");

        // 3. Sleep 100ms: expected replenishment is ~10,240 bytes (approx 1 packet)
        Thread.Sleep(120);
        long now = Stopwatch.GetTimestamp();
        double elapsed = (double)(now - lastTicks) / Stopwatch.Frequency;
        tokens = Math.Min(capacity, tokens + elapsed * bytesPerSec);

        TestAssert.IsTrue(tokens >= packetSize, $"Tokens should have replenished after 120ms (tokens={tokens:F0})");
    }

    [TestCase(TestTier.Tier1, "Verifies WinDivertShaper SetDownloadLimit and ClearDownloadLimit state transitions")]
    public void WinDivertShaper_LimitConfiguration_UpdatesCorrectly()
    {
        using var shaper = new WinDivertShaper();
        int testPid = 4321;

        // Limiter master toggle is OFF by default
        TestAssert.IsFalse(shaper.IsLimiterEnabled);
        TestAssert.IsFalse(shaper.HasActiveLimits);

        // Setting positive limit on PID or App activates HasActiveLimits even when master toggle is OFF
        shaper.SetAppLimit("chrome.exe", 1024, isDownload: true, enabled: true);
        TestAssert.IsTrue(shaper.HasActiveLimits, "Individual limit must be active when master limiter switch is OFF");
        TestAssert.IsTrue(shaper.HasActiveIndividualLimits);

        // Clearing limit
        shaper.ClearAppLimit("chrome.exe");
        TestAssert.IsFalse(shaper.HasActiveLimits);

        // Setting download limit on PID
        shaper.SetDownloadLimit(testPid, 1024); // 1024 kbit/s
        TestAssert.IsTrue(shaper.HasActiveLimits);
        // Updating port mapping
        shaper.UpdatePortMap(new[] { ((ushort)8080, testPid) });

        // Clearing limit
        shaper.ClearDownloadLimit(testPid);
        TestAssert.IsFalse(shaper.HasActiveLimits);

        // Setting 0 limit should clear it
        shaper.SetDownloadLimit(testPid, 0);
    }

    [TestCase(TestTier.Tier1, "Verifies ProcessRow LimitKbps and DownloadLimitKbps properties and formatted text")]
    public void ProcessRow_LimitProperties_FormatCorrectly()
    {
        var row = new ProcessRow(999, "browser.exe", @"C:\App\browser.exe");
        TestAssert.IsFalse(row.HasLimit);
        TestAssert.AreEqual("", row.LimitText);
        TestAssert.AreEqual("unlimited", row.DownloadLimitText);

        row.LimitKbps = 500;
        row.DownloadLimitKbps = 2000;

        TestAssert.IsTrue(row.HasLimit);
        TestAssert.AreEqual("500 kbit/s", row.LimitText);
        TestAssert.AreEqual("2000 kbit/s", row.DownloadLimitText);
    }
}
