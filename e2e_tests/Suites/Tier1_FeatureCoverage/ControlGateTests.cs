using System;
using System.IO;
using System.Threading.Tasks;
using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class ControlGateTests
{
    [TestCase(TestTier.Tier1, "Verifies non-elevated user permissions allow monitoring but block mutations with permission message")]
    public void ControlGate_NonElevatedUser_CannotMutate_CanMonitor()
    {
        // 1. Verify Windows ACL role evaluation for standard non-elevated identity
        var settings = new Settings();
        var defaults = WindowsAclManager.GetDefaultPermissions();
        var standardUser = defaults.Find(p => p.AccountName.Contains("Users") || p.AccountName.Contains("Everyone"));
        TestAssert.IsNotNull(standardUser, "Default permissions must contain standard user or everyone rule");
        TestAssert.IsTrue(standardUser!.AllowMonitor, "Standard user must be allowed to monitor traffic");
        TestAssert.IsFalse(standardUser.AllowControl, "Standard user must NOT be allowed to control/mutate rules");

        // 2. Setup Gated Control Context with non-elevated permissions (CanControl = false)
        var context = new GatedControlContext(canControl: false);

        // Add dummy rows to monitoring collections
        var proc = new ProcessRow(5678, "chrome.exe", @"C:\Program Files\Google\Chrome\chrome.exe");
        context.Processes.Add(proc);
        context.FilterRules.Add(new FilterRule("Web HTTP", FilterConditionType.PortRange, "80"));

        // Assert monitoring collections are accessible and readable
        TestAssert.AreEqual(1, context.Processes.Count, "Processes collection must be readable");
        TestAssert.AreEqual("chrome.exe", context.Processes[0].Name);
        TestAssert.AreEqual(1, context.FilterRules.Count, "FilterRules collection must be readable");
        TestAssert.IsNotNull(context.Connections, "Connections collection must be readable");
        TestAssert.IsNotNull(context.Rules, "Rules collection must be readable");

        // 3. Assert Mutators are rejected with exact error message
        bool limiterResult = context.ToggleLimiter();
        TestAssert.IsFalse(limiterResult, "ToggleLimiter must fail when CanControl is false");
        TestAssert.AreEqual(ControlGate.PermissionDeniedMessage, context.Status);

        bool prioritiesResult = context.TogglePriorities();
        TestAssert.IsFalse(prioritiesResult, "TogglePriorities must fail when CanControl is false");
        TestAssert.AreEqual(ControlGate.PermissionDeniedMessage, context.Status);

        bool blockerResult = context.ToggleBlocker();
        TestAssert.IsFalse(blockerResult, "ToggleBlocker must fail when CanControl is false");
        TestAssert.AreEqual(ControlGate.PermissionDeniedMessage, context.Status);

        bool actionResult = context.SetBlockerAction("Deny");
        TestAssert.IsFalse(actionResult, "SetBlockerAction must fail when CanControl is false");
        TestAssert.AreEqual(ControlGate.PermissionDeniedMessage, context.Status);

        bool globalLimitsResult = context.SetGlobalLimits(5000, 2000);
        TestAssert.IsFalse(globalLimitsResult, "SetGlobalLimits must fail when CanControl is false");
        TestAssert.AreEqual(ControlGate.PermissionDeniedMessage, context.Status);

        // Test app limit mutations blocked
        bool appLimitResult = context.SetAppLimit(proc, 1500);
        TestAssert.IsFalse(appLimitResult, "SetAppLimit must fail when CanControl is false");
        TestAssert.AreEqual(0, proc.LimitKbps, "Upload LimitKbps must remain 0 when mutation blocked");
        TestAssert.AreEqual(ControlGate.PermissionDeniedMessage, context.Status);

        // Test filter additions blocked
        int initialFilters = context.FilterRules.Count;
        bool addFilterResult = context.AddFilterRule(new FilterRule("DNS", FilterConditionType.PortRange, "53"));
        TestAssert.IsFalse(addFilterResult, "AddFilterRule must fail when CanControl is false");
        TestAssert.AreEqual(initialFilters, context.FilterRules.Count, "Filter rules count must not increase");
        TestAssert.AreEqual(ControlGate.PermissionDeniedMessage, context.Status);

        // 4. Assert mutations succeed when CanControl = true (Elevated / Admin context)
        context.CanControl = true;
        bool adminLimiter = context.ToggleLimiter();
        TestAssert.IsTrue(adminLimiter, "ToggleLimiter must succeed when CanControl is true");
        bool adminFilter = context.AddFilterRule(new FilterRule("DNS", FilterConditionType.PortRange, "53"));
        TestAssert.IsTrue(adminFilter, "AddFilterRule must succeed when CanControl is true");
        TestAssert.AreEqual(initialFilters + 1, context.FilterRules.Count);
    }
}
