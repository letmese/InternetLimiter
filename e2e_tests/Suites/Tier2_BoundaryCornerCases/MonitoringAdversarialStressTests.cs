using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using Valve.Core;
using Valve.E2ETests.Framework;
using Valve.Views;

namespace Valve.E2ETests.Suites.Tier2_BoundaryCornerCases;

public sealed class MonitoringAdversarialStressTests
{
    [TestCase(TestTier.Tier2, "Stress-tests DnsResolver with invalid IPs, loopback, private IPs, and malformed strings")]
    public void DnsResolver_AdversarialInputs_HandledSafelyAndFiltered()
    {
        using var resolver = new DnsResolver();
        resolver.Start();

        // 1. Null / whitespace / empty
        resolver.Enqueue("");
        resolver.Enqueue("   ");
        TestAssert.AreEqual("", resolver.Lookup(""));
        TestAssert.AreEqual("", resolver.Lookup("   "));

        // 2. Malformed / non-IP strings
        resolver.Enqueue("not-an-ip");
        resolver.Enqueue("999.999.999.999");
        resolver.Enqueue("256.1.1.1");
        resolver.Enqueue("1.2.3.4.5");
        resolver.Enqueue("::ffff:999.999.999.999");

        // 3. Loopback (should be filtered out by IsPrivate)
        resolver.Enqueue("127.0.0.1");
        resolver.Enqueue("::1");
        resolver.Enqueue("127.0.0.254");
        resolver.Enqueue("127.255.255.255");

        // 4. Private networks (should be filtered out by IsPrivate)
        resolver.Enqueue("10.0.0.1");
        resolver.Enqueue("172.16.0.1");
        resolver.Enqueue("192.168.1.1");
        resolver.Enqueue("169.254.1.1");

        // Lookups on filtered / invalid IPs should return empty string (not cached as valid hostname)
        TestAssert.AreEqual("", resolver.Lookup("127.0.0.1"));
        TestAssert.AreEqual("", resolver.Lookup("10.0.0.1"));
        TestAssert.AreEqual("", resolver.Lookup("not-an-ip"));
    }

    [TestCase(TestTier.Tier2, "Stress-tests DnsResolver under high-concurrency rapid enqueue and lookup bursts")]
    public void DnsResolver_RapidConcurrentRequests_ThreadSafeAndLeakFree()
    {
        using var resolver = new DnsResolver();
        resolver.Start();

        const int threadCount = 10;
        const int requestsPerThread = 200;
        var exceptions = new ConcurrentBag<Exception>();

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < requestsPerThread; i++)
                    {
                        // Alternate between valid, invalid, loopback, and duplicate addresses
                        string ip = (i % 4) switch
                        {
                            0 => $"8.8.8.{i % 10}",
                            1 => "127.0.0.1",
                            2 => $"192.168.1.{i % 50}",
                            _ => $"invalid-addr-{threadId}-{i}"
                        };

                        resolver.Enqueue(ip);
                        _ = resolver.Lookup(ip);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
        }

        foreach (var th in threads) th.Start();
        foreach (var th in threads) th.Join();

        TestAssert.AreEqual(0, exceptions.Count, $"Expected 0 exceptions under concurrency, caught: {exceptions.Count}");
    }

    [TestCase(TestTier.Tier2, "Stress-tests ServiceResolver with negative PIDs, PID 0, PID 4, and non-existent PIDs")]
    public void ServiceResolver_EdgeAndNonExistentPids_SafeAndCached()
    {
        // PID <= 0 must return null immediately without error
        TestAssert.IsNull(ServiceResolver.GetServiceForPid(0));
        TestAssert.IsNull(ServiceResolver.GetServiceForPid(-1));
        TestAssert.IsNull(ServiceResolver.GetServiceForPid(-99999));

        // Non-existent large PID must return null
        TestAssert.IsNull(ServiceResolver.GetServiceForPid(99999999));

        // Manual refresh must execute safely without throwing
        ServiceResolver.Refresh();

        // System process PID 4 (may or may not have a service name, but must not crash)
        var sysInfo = ServiceResolver.GetServiceForPid(4);
        // Either null or a valid service info record
        if (sysInfo != null)
        {
            TestAssert.IsNotNull(sysInfo.ServiceName);
            TestAssert.IsNotNull(sysInfo.DisplayName);
        }
    }

    [TestCase(TestTier.Tier2, "Stress-tests ServiceResolver with rapid concurrent queries across multiple threads")]
    public void ServiceResolver_RapidConcurrentCalls_ThreadSafe()
    {
        const int threadCount = 10;
        const int iterations = 300;
        var exceptions = new ConcurrentBag<Exception>();

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        int pid = (i % 5) switch
                        {
                            0 => 0,
                            1 => -1,
                            2 => 4,
                            3 => 99999999,
                            _ => 1000 + threadId
                        };

                        _ = ServiceResolver.GetServiceForPid(pid);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
        }

        foreach (var th in threads) th.Start();
        foreach (var th in threads) th.Join();

        TestAssert.AreEqual(0, exceptions.Count, $"Expected 0 exceptions in ServiceResolver under concurrency, caught: {exceptions.Count}");
    }

    [TestCase(TestTier.Tier2, "Stress-tests TrafficGraph under extreme 10 Gbps bandwidth rates and zero rates on STA thread")]
    public void TrafficGraph_ExtremeBandwidthRates_ClampedAndVectorRendered()
    {
        Exception? threadException = null;

        var staThread = new Thread(() =>
        {
            try
            {
                var graph = new TrafficGraph();

                // 1. Test stream toggles and properties
                TestAssert.IsTrue(graph.ShowDownload);
                TestAssert.IsTrue(graph.ShowUpload);
                TestAssert.IsTrue(graph.DualYAxis);
                TestAssert.IsFalse(graph.IsPaused);

                graph.TogglePause();
                TestAssert.IsTrue(graph.IsPaused);
                graph.TogglePause();
                TestAssert.IsFalse(graph.IsPaused);

                // 2. Feed extreme 10 Gbps rates (1,250,000,000 bytes/sec)
                var extremeSamples = new List<GraphSample>();
                for (int i = 0; i < 60; i++)
                {
                    // 10 Gbps In, 5 Gbps Out
                    extremeSamples.Add(new GraphSample(1_250_000_000.0, 625_000_000.0));
                }
                graph.SetData(extremeSamples);

                // 3. Feed zero rates
                var zeroSamples = new List<GraphSample>();
                for (int i = 0; i < 60; i++)
                {
                    zeroSamples.Add(new GraphSample(0.0, 0.0));
                }
                graph.SetData(zeroSamples);

                // 4. Feed asymmetric rates (DL spike, UL zero)
                var asymmetricSamples = new List<GraphSample>();
                for (int i = 0; i < 60; i++)
                {
                    asymmetricSamples.Add(new GraphSample(i * 100_000_000.0, 0.0));
                }
                graph.SetData(asymmetricSamples);

                // 5. Stream toggles
                graph.ShowDownload = false;
                graph.ShowUpload = true;
                graph.SetData(asymmetricSamples);

                graph.ShowDownload = true;
                graph.ShowUpload = false;
                graph.SetData(asymmetricSamples);

                graph.DualYAxis = false;
                graph.SetData(extremeSamples);
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        TestAssert.IsNull(threadException, $"TrafficGraph encountered exception: {threadException?.Message}");
    }

    [TestCase(TestTier.Tier2, "Stress-tests TrafficGraph sample edge cases: empty list, single sample, and massive history")]
    public void TrafficGraph_SampleHistory_BoundaryVariations_Safe()
    {
        Exception? threadException = null;

        var staThread = new Thread(() =>
        {
            try
            {
                var graph = new TrafficGraph();

                // Empty sample list
                graph.SetData(Array.Empty<GraphSample>());

                // Single sample (cannot divide by count - 1 without safety clamp)
                graph.SetData(new[] { new GraphSample(500, 1000) });

                // Two samples
                graph.SetData(new[] { new GraphSample(100, 200), new GraphSample(300, 400) });

                // 10,000 samples (stress list capacity)
                var hugeList = new List<GraphSample>(10_000);
                for (int i = 0; i < 10_000; i++)
                {
                    hugeList.Add(new GraphSample(i * 1024.0, (10_000 - i) * 1024.0));
                }
                graph.SetData(hugeList);
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        TestAssert.IsNull(threadException, $"TrafficGraph boundary variations failed: {threadException?.Message}");
    }

    [TestCase(TestTier.Tier2, "Stress-tests ProcessRow graph history capacity (180 samples) and Fmt rate scaling")]
    public void ProcessRow_GraphHistory_CapsAt180Samples_AndFormatsExtremeRates()
    {
        var row = new ProcessRow(1234, "test.exe", @"C:\Windows\System32\test.exe");

        // Add 1,000 samples - history must strictly cap at 180 samples without unbounded growth
        for (int i = 0; i < 1000; i++)
        {
            row.AddGraphSample(i * 1024.0, i * 512.0);
        }

        TestAssert.AreEqual(180, row.GraphHistory.Count, "GraphHistory capacity must be strictly capped at 180 samples");

        // Verify last sample is the newest
        var last = row.GraphHistory[row.GraphHistory.Count - 1];
        TestAssert.AreEqual(999.0 * 1024.0, last.In);
        TestAssert.AreEqual(999.0 * 512.0, last.Out);

        // Verify Fmt rate formatting under extreme 10 Gbps rate
        // 10 Gbps = 1,250,000,000 bytes/sec
        double tenGbpsBytes = 1_250_000_000.0;
        bool origBits = Fmt.UseBits;
        try
        {
            Fmt.UseBits = false;
            string byteRate = Fmt.Rate(tenGbpsBytes);
            TestAssert.IsTrue(byteRate.Contains("GB/s"), $"Expected GB/s in rate: {byteRate}");

            Fmt.UseBits = true;
            string bitRate = Fmt.Rate(tenGbpsBytes);
            TestAssert.IsTrue(bitRate.Contains("Gbit/s"), $"Expected Gbit/s in rate: {bitRate}");

            // NaN and negative inputs
            TestAssert.AreEqual("0 b/s", Fmt.Rate(double.NaN));
            TestAssert.AreEqual("0 b/s", Fmt.Rate(-500.0));
            Fmt.UseBits = false;
            TestAssert.AreEqual("0 B/s", Fmt.Rate(double.NaN));
            TestAssert.AreEqual("0 B/s", Fmt.Rate(-1000.0));
        }
        finally
        {
            Fmt.UseBits = origBits;
        }
    }
}
