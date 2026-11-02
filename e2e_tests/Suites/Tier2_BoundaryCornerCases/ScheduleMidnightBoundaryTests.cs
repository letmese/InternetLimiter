using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier2_BoundaryCornerCases;

public sealed class ScheduleMidnightBoundaryTests
{
    [TestCase(TestTier.Tier2, "Verifies schedule crossing midnight (22:00 to 02:00) is active before midnight")]
    public void Schedule_CrossingMidnight_ActiveBeforeMidnight()
    {
        var overnight = new ScheduleRule
        {
            App = "backup.exe",
            LimitKbps = 100,
            StartMinute = 22 * 60, // 22:00
            EndMinute = 2 * 60,    // 02:00
            Days = new[] { true, true, true, true, true, true, true },
            Enabled = true
        };

        // 23:45 on Tuesday
        var beforeMidnight = new DateTime(2026, 10, 27, 23, 45, 0);
        TestAssert.IsTrue(Policy.IsActive(overnight, beforeMidnight));
    }

    [TestCase(TestTier.Tier2, "Verifies schedule crossing midnight (22:00 to 02:00) is active after midnight")]
    public void Schedule_CrossingMidnight_ActiveAfterMidnight()
    {
        var overnight = new ScheduleRule
        {
            App = "backup.exe",
            LimitKbps = 100,
            StartMinute = 22 * 60, // 22:00
            EndMinute = 2 * 60,    // 02:00
            Days = new[] { true, true, true, true, true, true, true },
            Enabled = true
        };

        // 01:15 on Wednesday morning (previousDay was Tuesday, which was enabled)
        var afterMidnight = new DateTime(2026, 10, 28, 1, 15, 0);
        TestAssert.IsTrue(Policy.IsActive(overnight, afterMidnight));
    }

    [TestCase(TestTier.Tier2, "Verifies schedule crossing midnight is inactive outside the window during daytime")]
    public void Schedule_CrossingMidnight_InactiveAtNoon()
    {
        var overnight = new ScheduleRule
        {
            App = "backup.exe",
            LimitKbps = 100,
            StartMinute = 22 * 60,
            EndMinute = 2 * 60,
            Days = new[] { true, true, true, true, true, true, true },
            Enabled = true
        };

        var noon = new DateTime(2026, 10, 28, 12, 0, 0);
        TestAssert.IsFalse(Policy.IsActive(overnight, noon));
    }

    [TestCase(TestTier.Tier2, "Verifies boundary minutes: exact StartMinute is active, exact EndMinute is inactive")]
    public void Schedule_ExactMinuteBoundaries_StartInclusiveEndExclusive()
    {
        var rule = new ScheduleRule
        {
            App = "app.exe",
            LimitKbps = 300,
            StartMinute = 9 * 60,  // 09:00 (minute 540)
            EndMinute = 17 * 60,   // 17:00 (minute 1020)
            Days = new[] { true, true, true, true, true, true, true },
            Enabled = true
        };

        // 09:00:00 -> exactly at start (Active)
        var atStart = new DateTime(2026, 10, 27, 9, 0, 0);
        TestAssert.IsTrue(Policy.IsActive(rule, atStart));

        // 17:00:00 -> exactly at end (Inactive - end minute is exclusive)
        var atEnd = new DateTime(2026, 10, 27, 17, 0, 0);
        TestAssert.IsFalse(Policy.IsActive(rule, atEnd));

        // 16:59:59 -> last minute inside (Active)
        var justBeforeEnd = new DateTime(2026, 10, 27, 16, 59, 59);
        TestAssert.IsTrue(Policy.IsActive(rule, justBeforeEnd));
    }

    [TestCase(TestTier.Tier2, "Verifies degenerate schedule with equal StartMinute and EndMinute is never active")]
    public void Schedule_EqualStartAndEnd_ReturnsFalse()
    {
        var rule = new ScheduleRule
        {
            App = "app.exe",
            LimitKbps = 300,
            StartMinute = 10 * 60,
            EndMinute = 10 * 60,
            Days = new[] { true, true, true, true, true, true, true },
            Enabled = true
        };

        var atTen = new DateTime(2026, 10, 27, 10, 0, 0);
        TestAssert.IsFalse(Policy.IsActive(rule, atTen));
    }
}
