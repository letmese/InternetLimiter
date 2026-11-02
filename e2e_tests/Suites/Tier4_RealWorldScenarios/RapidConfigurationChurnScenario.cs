using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier4_RealWorldScenarios;

public sealed class RapidConfigurationChurnScenario
{
    [TestCase(TestTier.Tier4, "Verifies high-frequency rule, schedule, and priority mutations under concurrent flow evaluation")]
    public void RapidConfigurationChurn_ConcurrentReadsAndWrites_ZeroCollisions()
    {
        var rulesLock = new object();
        var rules = new List<FilterRule>
        {
            new("Initial Rule 1", FilterConditionType.PortRange, "80"),
            new("Initial Rule 2", FilterConditionType.ApplicationPath, "steam")
        };

        var schedulesLock = new object();
        var schedules = new List<ScheduleRule>
        {
            new()
            {
                App = "steam.exe",
                LimitKbps = 1000,
                StartMinute = 0,
                EndMinute = 1440,
                Days = new[] { true, true, true, true, true, true, true },
                Enabled = true
            }
        };

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var token = cts.Token;
        int evaluatedFlows = 0;
        int mutationCount = 0;

        // 4 concurrent reader threads simulating high packet flow rate evaluating rules
        var readers = new Task[4];
        for (int i = 0; i < readers.Length; i++)
        {
            readers[i] = Task.Run(() =>
            {
                var now = new DateTime(2026, 10, 27, 12, 0, 0);
                while (!token.IsCancellationRequested)
                {
                    // Flow evaluation snapshot
                    List<FilterRule> ruleSnapshot;
                    lock (rulesLock)
                    {
                        ruleSnapshot = new List<FilterRule>(rules);
                    }

                    foreach (var r in ruleSnapshot)
                    {
                        r.Matches(@"C:\Games\steam.exe", "1.2.3.4", 80, "TCP", "", "", false);
                    }

                    // Schedule evaluation snapshot
                    List<ScheduleRule> schedSnapshot;
                    lock (schedulesLock)
                    {
                        schedSnapshot = new List<ScheduleRule>(schedules);
                    }

                    Policy.EffectiveLimit("steam.exe", 0, schedSnapshot, now);
                    Interlocked.Increment(ref evaluatedFlows);
                }
            });
        }

        // Writer thread rapidly creating, updating, and removing rules
        var writer = Task.Run(() =>
        {
            int counter = 0;
            while (!token.IsCancellationRequested)
            {
                lock (rulesLock)
                {
                    rules.Add(new FilterRule($"Dynamic {counter}", FilterConditionType.PortRange, (8000 + counter).ToString())
                    {
                        Priority = (counter % 2 == 0) ? TrafficPriority.High : TrafficPriority.Low
                    });
                    if (rules.Count > 20)
                    {
                        rules.RemoveAt(0);
                    }
                }

                lock (schedulesLock)
                {
                    schedules[0].LimitKbps = 500 + (counter % 1000);
                }

                counter++;
                Interlocked.Increment(ref mutationCount);
            }
        });

        Task.WaitAll(new[] { writer }.Concat(readers).ToArray());

        TestAssert.IsTrue(evaluatedFlows > 100, $"Expected high throughput evaluations during churn (actual: {evaluatedFlows})");
        TestAssert.IsTrue(mutationCount > 10, $"Expected multiple mutations during churn (actual: {mutationCount})");
    }
}
