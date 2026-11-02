using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;

namespace Valve.Core;

/// <summary>
/// Model for a Windows User Account or Group permission rule (NetLimiter Sec 16, Items 16.01-16.03).
/// </summary>
public sealed class WindowsUserPermission
{
    public string AccountName { get; set; } = "";
    public bool AllowMonitor { get; set; } = true;
    public bool AllowControl { get; set; }
    public bool AllowRemoteConnect { get; set; }

    public WindowsUserPermission() { }

    public WindowsUserPermission(string accountName, bool monitor, bool control, bool remoteConnect)
    {
        AccountName = accountName;
        AllowMonitor = monitor;
        AllowControl = control;
        AllowRemoteConnect = remoteConnect;
    }
}

/// <summary>
/// Enforces permissions based on Windows user accounts and groups (BUILTIN\Administrators, BUILTIN\Users, etc.).
/// Evaluates Monitor (view only), Control (create/edit rules, limits, kill conns), and Remote Connect.
/// </summary>
public static class WindowsAclManager
{
    public static List<WindowsUserPermission> GetDefaultPermissions()
    {
        return new List<WindowsUserPermission>
        {
            new(@"BUILTIN\Administrators", monitor: true, control: true, remoteConnect: true),
            new(@"BUILTIN\Users", monitor: true, control: false, remoteConnect: false),
            new(@"Everyone", monitor: true, control: false, remoteConnect: false)
        };
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static (bool CanMonitor, bool CanControl, bool CanRemoteConnect) EvaluateIdentity(Settings settings, WindowsIdentity? identity = null)
    {
        var rules = settings.WindowsPermissions;
        if (rules == null || rules.Count == 0)
        {
            rules = GetDefaultPermissions();
        }

        string currentUserName = Environment.UserName;
        string currentUserDomain = Environment.UserDomainName;
        string fullAccount = $"{currentUserDomain}\\{currentUserName}";

        bool isAdmin = false;
        try
        {
            var id = identity ?? WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { }

        // 1. Direct match for current user account
        var userRule = rules.FirstOrDefault(r => string.Equals(r.AccountName, fullAccount, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(r.AccountName, currentUserName, StringComparison.OrdinalIgnoreCase));
        if (userRule != null)
        {
            return (userRule.AllowMonitor, userRule.AllowControl, userRule.AllowRemoteConnect);
        }

        // 2. Administrators group match
        if (isAdmin)
        {
            var adminRule = rules.FirstOrDefault(r => r.AccountName.Contains("Administrators", StringComparison.OrdinalIgnoreCase));
            if (adminRule != null)
            {
                return (adminRule.AllowMonitor, adminRule.AllowControl, adminRule.AllowRemoteConnect);
            }
            return (true, true, true);
        }

        // 3. Users group match
        var usersRule = rules.FirstOrDefault(r => r.AccountName.Contains("Users", StringComparison.OrdinalIgnoreCase));
        if (usersRule != null)
        {
            return (usersRule.AllowMonitor, usersRule.AllowControl, usersRule.AllowRemoteConnect);
        }

        // 4. Fallback to Everyone
        var everyoneRule = rules.FirstOrDefault(r => string.Equals(r.AccountName, "Everyone", StringComparison.OrdinalIgnoreCase));
        if (everyoneRule != null)
        {
            return (everyoneRule.AllowMonitor, everyoneRule.AllowControl, everyoneRule.AllowRemoteConnect);
        }

        return (true, false, false); // Default safe fallback: Monitor only
    }

    public static bool CanMonitor(Settings settings) => EvaluateIdentity(settings).CanMonitor;
    public static bool CanControl(Settings settings) => EvaluateIdentity(settings).CanControl;
    public static bool CanRemoteConnect(Settings settings) => EvaluateIdentity(settings).CanRemoteConnect;
}
