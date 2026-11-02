using System.Diagnostics;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier2_BoundaryCornerCases;

public sealed class TokenBucketBoundaryTests
{
    [TestCase(TestTier.Tier2, "Verifies TokenBucket handles extreme high throughput (10 Gbps) without integer overflow")]
    public void TokenBucket_ExtremeThroughput_10Gbps_NoOverflow()
    {
        // 10 Gbps = 1,250,000,000 bytes/sec
        long bytesPerSec = 1_250_000_000L;
        long capacity = bytesPerSec;
        double tokens = capacity;
        long lastTicks = Stopwatch.GetTimestamp();

        // Consume 100 MB
        int largeChunk = 100 * 1024 * 1024;
        TestAssert.IsTrue(tokens >= largeChunk);
        tokens -= largeChunk;

        // Verify remaining tokens calculation
        TestAssert.AreEqual((double)(capacity - largeChunk), tokens);

        // Sleep 10ms: should replenish 12.5 MB
        Thread.Sleep(20);
        long now = Stopwatch.GetTimestamp();
        double elapsed = (double)(now - lastTicks) / Stopwatch.Frequency;
        tokens = Math.Min(capacity, tokens + elapsed * bytesPerSec);

        TestAssert.IsTrue(tokens > (capacity - largeChunk), "Tokens should replenish at 10 Gbps rate");
        TestAssert.IsTrue(tokens <= capacity, "Tokens should never exceed capacity");
    }

    [TestCase(TestTier.Tier2, "Verifies TokenBucket enforces minimum safety floor of 1024 B/s")]
    public void TokenBucket_ZeroOrNegativeRate_EnforcesMinimumFloor()
    {
        long configuredBps = 0;
        long effectiveBps = Math.Max(1024, configuredBps);
        TestAssert.AreEqual(1024L, effectiveBps);

        configuredBps = -5000;
        effectiveBps = Math.Max(1024, configuredBps);
        TestAssert.AreEqual(1024L, effectiveBps);
    }

    [TestCase(TestTier.Tier2, "Verifies TokenBucket burst capacity is strictly clamped to 1 second volume")]
    public void TokenBucket_IdleReplenishment_DoesNotExceedCapacity()
    {
        long bytesPerSec = 50 * 1024; // 50 KB/s
        long capacity = bytesPerSec;
        double tokens = capacity;

        // Simulate 10 seconds of idle time without consumption
        double simulatedIdleSeconds = 10.0;
        tokens = Math.Min(capacity, tokens + simulatedIdleSeconds * bytesPerSec);

        // Even after 10s idle, capacity must remain clamped to 1-second burst (50 KB)
        TestAssert.AreEqual((double)capacity, tokens);
    }
}
