namespace Valve.Core;

/// <summary>One remote-command permission entry (ledger 15.x). Each entry names a command type
/// and whether it is allowed. Absent entries fall back to the built-in default policy.</summary>
public sealed class PermissionEntry
{
    /// <summary>Command type, upper-cased, e.g. SETBLOCK, RESET, SNAPSHOT.</summary>
    public string Type { get; set; } = "";

    /// <summary>True = allow this command from remote, false = deny.</summary>
    public bool Allowed { get; set; } = true;

    /// <summary>Free-form note shown in the Permissions window.</summary>
    public string Note { get; set; } = "";

    public string StateText => Allowed ? "Allow" : "Deny";
}
