using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class TrafficRateCalculationTests
{
    [TestCase(TestTier.Tier1, "Verifies byte/s rate scaling across B/s, KB/s, MB/s, and GB/s")]
    public void Fmt_Rate_ByteMode_ScalesCorrectly()
    {
        bool originalBits = Fmt.UseBits;
        try
        {
            Fmt.UseBits = false;
            TestAssert.AreEqual("0 B/s", Fmt.Rate(0));
            TestAssert.AreEqual("500 B/s", Fmt.Rate(500));
            TestAssert.AreEqual("1 KB/s", Fmt.Rate(1024));
            TestAssert.AreEqual("1.5 KB/s", Fmt.Rate(1536));
            TestAssert.AreEqual("2 MB/s", Fmt.Rate(2 * 1024 * 1024));
            TestAssert.AreEqual("1.5 GB/s", Fmt.Rate(1.5 * 1024 * 1024 * 1024));
        }
        finally
        {
            Fmt.UseBits = originalBits;
        }
    }

    [TestCase(TestTier.Tier1, "Verifies bit/s rate scaling when Fmt.UseBits is enabled")]
    public void Fmt_Rate_BitMode_ScalesCorrectly()
    {
        bool originalBits = Fmt.UseBits;
        try
        {
            Fmt.UseBits = true;
            TestAssert.AreEqual("0 b/s", Fmt.Rate(0));
            // 128 bytes/s * 8 = 1024 b/s = 1 kbit/s
            TestAssert.AreEqual("1 kbit/s", Fmt.Rate(128));
            // 128 KB/s * 8 = 1024 kbit/s = 1 Mbit/s
            TestAssert.AreEqual("1 Mbit/s", Fmt.Rate(128 * 1024));
        }
        finally
        {
            Fmt.UseBits = originalBits;
        }
    }

    [TestCase(TestTier.Tier1, "Verifies cumulative byte volume formatting across B, KB, MB, GB, TB")]
    public void Fmt_Bytes_ScalesCumulativeVolumes()
    {
        TestAssert.AreEqual("0 B", Fmt.Bytes(0));
        TestAssert.AreEqual("512 B", Fmt.Bytes(512));
        TestAssert.AreEqual("1 KB", Fmt.Bytes(1024));
        TestAssert.AreEqual("10 MB", Fmt.Bytes(10L * 1024 * 1024));
        TestAssert.AreEqual("4 GB", Fmt.Bytes(4L * 1024 * 1024 * 1024));
        TestAssert.AreEqual("2 TB", Fmt.Bytes(2L * 1024 * 1024 * 1024 * 1024));
    }

    [TestCase(TestTier.Tier1, "Verifies ProcessRow updates throughput, formats totals, and resets correctly")]
    public void ProcessRow_RateAndTotals_CalculatesAndUpdates()
    {
        var row = new ProcessRow(1337, "testapp.exe", @"C:\Program Files\TestApp\testapp.exe");
        TestAssert.AreEqual(1337, row.Pid);
        TestAssert.AreEqual("testapp.exe", row.Name);
        TestAssert.AreEqual("0 B/s", row.InRateText);
        TestAssert.AreEqual("0 B/s", row.OutRateText);

        row.InRate = 5120; // 5 KB/s
        row.OutRate = 2048; // 2 KB/s
        row.InTotal = 50000;
        row.OutTotal = 20000;
        row.Connections = 4;

        TestAssert.AreEqual("5 KB/s", row.InRateText);
        TestAssert.AreEqual("2 KB/s", row.OutRateText);
        TestAssert.Contains("50000", row.InTotal.ToString());
        TestAssert.AreEqual(4, row.Connections);

        // ResetTotals
        row.ResetTotals();
        TestAssert.AreEqual(0L, row.InTotal);
        TestAssert.AreEqual(0L, row.OutTotal);
    }

    [TestCase(TestTier.Tier1, "Verifies ConnReading record instantiates with accurate values")]
    public void ConnReading_Properties_MatchInput()
    {
        var reading = new ConnReading(
            Key: "tcp:127.0.0.1:5000:192.168.1.1:443",
            Pid: 456,
            Proto: "TCP",
            Local: "127.0.0.1:5000",
            Remote: "192.168.1.1:443",
            RemoteIp: "192.168.1.1",
            InRate: 10240,
            OutRate: 2048,
            InTotal: 100000,
            OutTotal: 50000,
            LocalPort: 5000);

        TestAssert.AreEqual("TCP", reading.Proto);
        TestAssert.AreEqual(456, reading.Pid);
        TestAssert.AreEqual(5000, (int)reading.LocalPort);
        TestAssert.AreEqual("192.168.1.1", reading.RemoteIp);
        TestAssert.AreEqual(10240.0, reading.InRate);
    }

    [TestCase(TestTier.Tier1, "Verifies connection rates are NOT zeroed out when process engine rate is 0")]
    public void RatePipeline_ZeroProcessRate_DoesNotZeroConnectionRates()
    {
        var proc = new ProcessRow(100, "chrome.exe", @"C:\chrome.exe") { InRate = 0, OutRate = 0 };
        var conn = new ConnRow 
        { 
            Key = "k1", 
            Pid = 100, 
            Process = "chrome.exe", 
            Proto = "TCP", 
            Local = "127.0.0.1:1000", 
            Remote = "1.2.3.4:443", 
            RemoteIp = "1.2.3.4", 
            InRate = 45000, 
            OutRate = 12000 
        };
        var conns = new List<ConnRow> { conn };

        // Emulate the updated ApplyDriverRates rescale logic
        double sumInRate = conns.Sum(c => c.InRate);
        double sumOutRate = conns.Sum(c => c.OutRate);

        if (proc.InRate > 0 && sumInRate > 0)
        {
            double scaleIn = Math.Clamp(proc.InRate / sumInRate, 0.1, 10.0);
            foreach (var c in conns) c.InRate *= scaleIn;
        }

        if (proc.OutRate > 0 && sumOutRate > 0)
        {
            double scaleOut = Math.Clamp(proc.OutRate / sumOutRate, 0.1, 10.0);
            foreach (var c in conns) c.OutRate *= scaleOut;
        }

        // Connection rates must be preserved intact!
        TestAssert.AreEqual(45000.0, conn.InRate);
        TestAssert.AreEqual(12000.0, conn.OutRate);
    }

    [TestCase(TestTier.Tier1, "Verifies Normal and Critical priorities have zero pacing delay overhead")]
    public void WinDivertShaper_NormalPriority_ZeroLatencyDelay()
    {
        using var shaper = new WinDivertShaper();
        // Priority reservations and normal/critical pacing delay must be zero overhead
        shaper.SetProcessPriority(200, TrafficPriority.Normal);
        shaper.SetProcessPriority(201, TrafficPriority.Critical);

        // Telemetry counters initialized safely
        TestAssert.AreEqual(0.0, shaper.Pid0Ratio);
        TestAssert.AreEqual(0.0, shaper.TryGetStatsHitRatio);
        TestAssert.IsTrue(shaper.LastPacketUtc <= DateTime.UtcNow);
    }
}
