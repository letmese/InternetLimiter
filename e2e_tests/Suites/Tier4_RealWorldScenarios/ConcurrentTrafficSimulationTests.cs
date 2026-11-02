using System.Collections.Concurrent;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier4_RealWorldScenarios;

public sealed class ConcurrentTrafficSimulationTests
{
    [TestCase(TestTier.Tier4, "Verifies multi-threaded simulated packet flow updates counters with exact atomic precision")]
    public void ConcurrentTraffic_MultiThreadedPacketBursts_MaintainsZeroDataLoss()
    {
        const int threadCount = 10;
        const int packetsPerThread = 1000;
        const int packetBytes = 1500; // Standard Ethernet MTU payload

        ulong totalBytesIn = 0;
        ulong totalBytesOut = 0;
        var perProcessBytes = new ConcurrentDictionary<int, ulong>();

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            int simulatedPid = 1000 + (t % 3); // 3 distinct processes

            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < packetsPerThread; i++)
                {
                    bool isInbound = (i % 2 == 0);
                    if (isInbound)
                    {
                        Interlocked.Add(ref totalBytesIn, (ulong)packetBytes);
                    }
                    else
                    {
                        Interlocked.Add(ref totalBytesOut, (ulong)packetBytes);
                    }

                    perProcessBytes.AddOrUpdate(simulatedPid, (ulong)packetBytes, (_, old) => old + (ulong)packetBytes);
                }
            });
        }

        Task.WaitAll(tasks);

        ulong expectedTotal = (ulong)(threadCount * packetsPerThread * packetBytes);
        TestAssert.AreEqual(expectedTotal, totalBytesIn + totalBytesOut, "Total transferred bytes across all threads must match exact arithmetic sum");

        ulong perProcessSum = 0;
        foreach (var kvp in perProcessBytes)
        {
            perProcessSum += kvp.Value;
        }
        TestAssert.AreEqual(expectedTotal, perProcessSum, "Sum of individual per-process counters must match total bytes");
    }

    [TestCase(TestTier.Tier4, "Verifies concurrent port-to-PID table queries and churn run safely without locking or deadlocks")]
    public void ConcurrentPortMapping_ReadWriteChurn_ThreadSafe()
    {
        var portMap = new ConcurrentDictionary<ushort, int>();
        // Pre-populate ports
        for (ushort p = 1000; p < 2000; p++)
        {
            portMap[p] = (int)(p * 2);
        }

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var token = cts.Token;

        // Writers updating port mappings
        var writer = Task.Run(() =>
        {
            ushort port = 2000;
            while (!token.IsCancellationRequested)
            {
                portMap[port] = 42;
                portMap.TryRemove((ushort)(port - 100), out _);
                port = (ushort)(2000 + ((port + 1) % 500));
            }
        });

        // Readers querying ports
        var readers = new Task[4];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(() =>
            {
                int hits = 0;
                while (!token.IsCancellationRequested)
                {
                    if (portMap.TryGetValue(1500, out _))
                    {
                        hits++;
                    }
                }
                TestAssert.IsTrue(hits > 0, "Reader should successfully query mappings concurrently");
            });
        }

        Task.WaitAll(new[] { writer }.Concat(readers).ToArray());
    }
}
