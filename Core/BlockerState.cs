namespace Valve.Core;

/// <summary>
/// Per-direction blocking decision, matching NetLimiter's Allow / Deny / Ask /
/// Not set vocabulary. Valve implements Not set, Allow, Deny and Ask.
/// Ask holds new connections in a pending queue until the user answers,
/// then resolves to an Allow or Deny decision (temporary or remembered).
/// </summary>
public enum BlockerState
{
    NotSet,
    Allow,
    Deny,
    Ask,
}

public static class BlockerStateText
{
    public static string Label(this BlockerState state) => state switch
    {
        BlockerState.Allow => "Allow",
        BlockerState.Deny => "Deny",
        BlockerState.Ask => "Ask",
        _ => "Not set",
    };

    /// <summary>Cycles Not set → Deny → Allow → Ask → Not set, matching the UI toolbar.</summary>
    public static BlockerState Next(this BlockerState state) => state switch
    {
        BlockerState.NotSet => BlockerState.Deny,
        BlockerState.Deny => BlockerState.Allow,
        BlockerState.Allow => BlockerState.Ask,
        _ => BlockerState.NotSet,
    };
}
