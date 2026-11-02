using System;
using System.Collections.Generic;

namespace Valve.Core;

/// <summary>
/// Gatekeeper for local and remote operations requiring Control permission (NetLimiter Sec 16).
/// Non-elevated or standard users can monitor traffic but cannot mutate rules, limits, or priorities.
/// </summary>
public static class ControlGate
{
    public const string PermissionDeniedMessage = "Control permission required (run elevated or ask an administrator)";

    /// <summary>
    /// Computes whether local control is permitted based on Windows ACLs and admin elevation.
    /// </summary>
    public static bool EvaluateCanControl(Settings settings)
    {
        try
        {
            var acl = WindowsAclManager.EvaluateIdentity(settings);
            bool isElevated = WindowsAclManager.IsAdministrator() || Interop.Native.IsUserAnAdmin();
            return acl.CanControl && isElevated;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks control permission against an explicit or computed capability flag.
    /// </summary>
    public static bool CheckPermission(bool canControl, out string? rejectionMessage)
    {
        if (!canControl)
        {
            rejectionMessage = PermissionDeniedMessage;
            return false;
        }

        rejectionMessage = null;
        return true;
    }
}

/// <summary>
/// Lightweight model representing the gated control and monitoring surface for verification.
/// </summary>
public sealed class GatedControlContext
{
    public bool CanControl { get; set; }
    public string Status { get; set; } = "Ready";

    public List<ProcessRow> Processes { get; } = new();
    public List<ConnRow> Connections { get; } = new();
    public List<FilterRule> FilterRules { get; } = new();
    public List<UnifiedRule> Rules { get; } = new();

    public bool LimiterEnabled { get; set; } = false;
    public bool PrioritiesEnabled { get; set; } = false;
    public bool BlockerEnabled { get; set; } = false;
    public string DefaultBlockerAction { get; set; } = "Allow";
    public int GlobalDownloadLimitKbps { get; set; } = 0;
    public int GlobalUploadLimitKbps { get; set; } = 0;

    public GatedControlContext(bool canControl)
    {
        CanControl = canControl;
    }

    private bool CheckPermission()
    {
        if (!ControlGate.CheckPermission(CanControl, out var msg))
        {
            Status = msg!;
            return false;
        }
        return true;
    }

    public bool ToggleLimiter()
    {
        if (!CheckPermission()) return false;
        LimiterEnabled = !LimiterEnabled;
        return true;
    }

    public bool TogglePriorities()
    {
        if (!CheckPermission()) return false;
        PrioritiesEnabled = !PrioritiesEnabled;
        return true;
    }

    public bool ToggleBlocker()
    {
        if (!CheckPermission()) return false;
        BlockerEnabled = !BlockerEnabled;
        return true;
    }

    public bool SetBlockerAction(string action)
    {
        if (!CheckPermission()) return false;
        DefaultBlockerAction = action;
        return true;
    }

    public bool SetGlobalLimits(int dlKbps, int ulKbps)
    {
        if (!CheckPermission()) return false;
        GlobalDownloadLimitKbps = dlKbps;
        GlobalUploadLimitKbps = ulKbps;
        return true;
    }

    public bool SetAppLimit(ProcessRow row, int kbps)
    {
        if (!CheckPermission()) return false;
        row.LimitKbps = kbps;
        return true;
    }

    public bool AddFilterRule(FilterRule rule)
    {
        if (!CheckPermission()) return false;
        FilterRules.Add(rule);
        return true;
    }
}
