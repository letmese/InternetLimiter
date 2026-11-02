using System;
using System.Collections.Generic;
using System.Linq;

namespace Valve.Core;

/// <summary>Policy guard for remote administration commands (ledger 15.x).
/// Read-only commands are always allowed; state-changing commands require either a
/// matching PermissionEntry with Allowed=true or, when no entry exists, the built-in default.</summary>
public static class PermissionGuard
{
    // State-changing commands that a remote peer may not run unless explicitly permitted.
    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "SETBLOCK", "RESET", "SETLIMIT", "SETPRIORITY", "KILL", "TERMINATE", "SETGLOBAL", "EMERGENCY", "REBOOT",
    };

    public static bool IsReadOnly(string type) => !Sensitive.Contains(type);

    public static bool IsAllowed(Settings settings, string type)
    {
        string upper = type.ToUpperInvariant();
        PermissionEntry? entry = settings.Permissions.FirstOrDefault(p =>
            string.Equals(p.Type, upper, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            return entry.Allowed;
        }

        return !Sensitive.Contains(upper); // deny by default for sensitive commands
    }

    public static string Describe(string type)
    {
        string upper = type.ToUpperInvariant();
        return Sensitive.Contains(upper) ? "state-changing (requires permission)" : "read-only";
    }
}
