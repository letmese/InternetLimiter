namespace Valve.Core;

/// <summary>
/// Pure decision logic for schedules and quotas. No I/O, no state — so it can be
/// reasoned about and tested independently of the running app.
/// </summary>
public static class Policy
{
    /// <summary>
    /// True when <paramref name="now"/> falls inside the rule's window on an enabled day.
    /// Handles windows that cross midnight (e.g. 22:00–02:00).
    /// </summary>
    public static bool IsActive(ScheduleRule rule, DateTime now)
    {
        if (!rule.Enabled || rule.LimitKbps <= 0)
        {
            return false;
        }

        int minute = (now.Hour * 60) + now.Minute;
        int day = (int)now.DayOfWeek; // Sunday = 0, matching the Days array

        if (rule.StartMinute == rule.EndMinute)
        {
            return false;
        }

        if (rule.StartMinute < rule.EndMinute)
        {
            return rule.Days[day] && minute >= rule.StartMinute && minute < rule.EndMinute;
        }

        // Crosses midnight: the tail belongs to the following day.
        bool inHead = minute >= rule.StartMinute;
        bool inTail = minute < rule.EndMinute;
        if (inHead)
        {
            return rule.Days[day];
        }

        int previousDay = (day + 6) % 7;
        return inTail && rule.Days[previousDay];
    }

    /// <summary>
    /// The cap that should currently apply to an app, given its static limit and
    /// every schedule that targets it. The tightest active window wins.
    /// </summary>
    public static int EffectiveLimit(string app, int configuredKbps, IEnumerable<ScheduleRule> schedules, DateTime now)
    {
        int limit = configuredKbps;
        foreach (ScheduleRule rule in schedules)
        {
            if (!string.Equals(rule.App, app, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsActive(rule, now))
            {
                continue;
            }

            limit = limit <= 0 ? rule.LimitKbps : Math.Min(limit, rule.LimitKbps);
        }

        return limit;
    }

    public static bool QuotaExceeded(AppRule rule, long usedIn, long usedOut)
    {
        if (rule.QuotaBytes <= 0)
        {
            return false;
        }

        return usedIn + usedOut >= rule.QuotaBytes;
    }

    public static double QuotaFraction(AppRule rule, long usedIn, long usedOut)
    {
        if (rule.QuotaBytes <= 0)
        {
            return 0;
        }

        return Math.Clamp((double)(usedIn + usedOut) / rule.QuotaBytes, 0, 1);
    }
}

public sealed class RuleEvaluationResult
{
    public bool IsBypassed { get; set; }
    public bool CountInStats { get; set; } = true;
    public BlockerAction BlockAction { get; set; } = BlockerAction.NotSet;
    public int EffectiveLimitKbps { get; set; }
    public TrafficPriority Priority { get; set; } = TrafficPriority.Normal;
    public bool QuotaExceeded { get; set; }
    public string QuotaAction { get; set; } = "Block";
    public List<UnifiedRule> MatchedRules { get; } = new();
}

/// <summary>
/// Deterministic Unified Rule Engine evaluation pipeline (Section 4, Items 4.01 - 4.10).
/// Precedence order: Ignore Rules > Blocker Rules > Quotas / Limits / Priorities.
/// </summary>
public static class RuleEvaluator
{
    public static RuleEvaluationResult Evaluate(IEnumerable<UnifiedRule> rules, ConnectionContext ctx, IReadOnlyList<FilterRule>? filters = null, DateTime? now = null)
    {
        var result = new RuleEvaluationResult();
        if (rules == null || ctx == null) return result;

        DateTime evaluationTime = now ?? DateTime.Now;
        var matchingRules = new List<UnifiedRule>();

        foreach (var rule in rules)
        {
            if (MatchesRule(rule, ctx, filters, evaluationTime))
            {
                matchingRules.Add(rule);
                result.MatchedRules.Add(rule);
            }
        }

        // --- PRECEDENCE TIER 1: IGNORE RULES (Item 4.03, 4.04) ---
        var ignoreRules = matchingRules.Where(r => r.Type == RuleType.Ignore).ToList();
        if (ignoreRules.Count > 0)
        {
            // If any IgnoreData rule matches, immediately wire-speed bypass without stats (VPN double-counting solution)
            if (ignoreRules.Any(r => r.IgnoreMode == IgnoreType.IgnoreData))
            {
                result.IsBypassed = true;
                result.CountInStats = false;
                result.EffectiveLimitKbps = 0;
                return result;
            }

            // If IgnoreLimit matches, bypass limits while remaining counted in stats
            if (ignoreRules.Any(r => r.IgnoreMode == IgnoreType.IgnoreLimit))
            {
                result.IsBypassed = true;
                result.CountInStats = true;
                result.EffectiveLimitKbps = 0;
            }
        }

        // --- PRECEDENCE TIER 2: BLOCKER RULES (Item 3.07, 4.04) ---
        var blockerRules = matchingRules.Where(r => r.Type == RuleType.Blocker).ToList();
        if (blockerRules.Count > 0)
        {
            // Sort by Weight descending, tie-break: Deny > Allow > Ask
            blockerRules.Sort((a, b) =>
            {
                int cmp = b.Weight.CompareTo(a.Weight);
                if (cmp != 0) return cmp;

                int ActionRank(BlockerAction s) => s switch
                {
                    BlockerAction.Deny => 0,
                    BlockerAction.Allow => 1,
                    BlockerAction.Ask => 2,
                    _ => 3
                };

                return ActionRank(a.BlockerAction).CompareTo(ActionRank(b.BlockerAction));
            });

            var winningBlocker = blockerRules[0];
            result.BlockAction = winningBlocker.BlockerAction;
            if (result.BlockAction == BlockerAction.Deny)
            {
                return result; // Packet blocked immediately
            }
        }

        // --- PRECEDENCE TIER 3: QUOTAS, LIMITS, PRIORITIES ---
        // 3a. Quotas
        var quotaRules = matchingRules.Where(r => r.Type == RuleType.Quota && r.QuotaBytes > 0).ToList();
        foreach (var qr in quotaRules)
        {
            if (qr.CurrentUsageIn + qr.CurrentUsageOut >= qr.QuotaBytes)
            {
                result.QuotaExceeded = true;
                result.QuotaAction = qr.QuotaExceededAction;
            }
        }

        // 3b. Limits
        if (!result.IsBypassed)
        {
            var limitRules = matchingRules.Where(r => r.Type == RuleType.Limit && r.LimitKbps > 0).ToList();
            if (limitRules.Count > 0)
            {
                result.EffectiveLimitKbps = limitRules.Min(r => r.LimitKbps);
            }
        }

        // 3c. Priorities
        if (!result.IsBypassed)
        {
            var priorityRules = matchingRules.Where(r => r.Type == RuleType.Priority).ToList();
            if (priorityRules.Count > 0)
            {
                result.Priority = priorityRules.Max(r => r.PriorityLevel);
            }
        }

        return result;
    }

    public static bool MatchesRule(UnifiedRule rule, ConnectionContext ctx, IReadOnlyList<FilterRule>? filters = null, DateTime? now = null)
    {
        if (rule == null || !rule.IsEnabled || ctx == null) return false;

        // Check scheduling (Item 4.08)
        if (now.HasValue && !IsScheduleActive(rule, now.Value))
        {
            return false;
        }

        // Check Direction
        if (rule.Direction == RuleDirection.In && !ctx.IsInbound) return false;
        if (rule.Direction == RuleDirection.Out && ctx.IsInbound) return false;

        // Check TargetZone
        if (!string.IsNullOrWhiteSpace(rule.TargetZone) &&
            !rule.TargetZone.Equals("All", StringComparison.OrdinalIgnoreCase) &&
            !rule.TargetZone.Equals("All Zones", StringComparison.OrdinalIgnoreCase))
        {
            if (rule.TargetZone.Equals("Local", StringComparison.OrdinalIgnoreCase) ||
                rule.TargetZone.Equals("Local Network", StringComparison.OrdinalIgnoreCase))
            {
                if (!ctx.IsLocal) return false;
            }
            else if (rule.TargetZone.Equals("Internet", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.IsLocal) return false;
            }
        }

        // Check TargetType
        if (rule.TargetType == RuleTargetType.WholeComputer || string.IsNullOrWhiteSpace(rule.TargetValue))
        {
            return true; // Whole-computer rule applies to all traffic (Item 4.05)
        }

        if (rule.TargetType == RuleTargetType.Application)
        {
            if (!string.IsNullOrEmpty(ctx.AppName) &&
                (ctx.AppName.Equals(rule.TargetValue, StringComparison.OrdinalIgnoreCase) ||
                 System.IO.Path.GetFileNameWithoutExtension(ctx.AppPath).Equals(rule.TargetValue, StringComparison.OrdinalIgnoreCase) ||
                 System.IO.Path.GetFileName(ctx.AppPath).Equals(rule.TargetValue, StringComparison.OrdinalIgnoreCase) ||
                 rule.TargetValue.Contains(ctx.AppName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(ctx.AppPath) &&
                ctx.AppPath.Equals(rule.TargetValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        if (rule.TargetType == RuleTargetType.Filter)
        {
            if (filters != null)
            {
                var targetFilter = filters.FirstOrDefault(f =>
                    string.Equals(f.Name, rule.TargetValue, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.Id, rule.TargetValue, StringComparison.OrdinalIgnoreCase));

                if (targetFilter != null)
                {
                    return targetFilter.Matches(ctx);
                }
            }
            return false;
        }

        if (rule.TargetType == RuleTargetType.Zone)
        {
            if (rule.TargetValue.Equals("Local", StringComparison.OrdinalIgnoreCase)) return ctx.IsLocal;
            if (rule.TargetValue.Equals("Internet", StringComparison.OrdinalIgnoreCase)) return !ctx.IsLocal;
            return true;
        }

        if (rule.TargetType == RuleTargetType.Connection)
        {
            return !string.IsNullOrEmpty(ctx.RemoteIp) && rule.TargetValue.Contains(ctx.RemoteIp);
        }

        return false;
    }

    public static bool IsScheduleActive(UnifiedRule rule, DateTime now)
    {
        if (rule == null) return false;
        if (rule.Schedules == null || rule.Schedules.Count == 0)
        {
            return rule.IsEnabled;
        }

        var startEntry = rule.Schedules.FirstOrDefault(s => s.Enabled && string.Equals(s.Action, "Start", StringComparison.OrdinalIgnoreCase));
        var stopEntry = rule.Schedules.FirstOrDefault(s => s.Enabled && string.Equals(s.Action, "Stop", StringComparison.OrdinalIgnoreCase));

        if (startEntry != null && stopEntry != null)
        {
            int minute = (now.Hour * 60) + now.Minute;
            int day = (int)now.DayOfWeek; // 0 = Sunday

            int startMin = startEntry.MinuteOfDay;
            int stopMin = stopEntry.MinuteOfDay;

            if (startMin == stopMin)
            {
                return false;
            }

            if (startMin < stopMin)
            {
                return startEntry.Days[day] && minute >= startMin && minute < stopMin;
            }
            else
            {
                // Midnight crossing window
                bool inHead = minute >= startMin;
                bool inTail = minute < stopMin;

                if (inHead)
                {
                    return startEntry.Days[day];
                }

                int prevDay = (day + 6) % 7;
                return inTail && startEntry.Days[prevDay];
            }
        }

        int curDay = (int)now.DayOfWeek;
        return rule.Schedules.Any(s => s.Enabled && s.Days[curDay]);
    }

    public static void EvaluateScheduleActions(IEnumerable<UnifiedRule> rules, DateTime now)
    {
        if (rules == null) return;

        int curDay = (int)now.DayOfWeek;
        int curMinute = (now.Hour * 60) + now.Minute;

        foreach (var rule in rules)
        {
            if (rule.Schedules == null) continue;

            foreach (var entry in rule.Schedules)
            {
                if (!entry.Enabled || !entry.Days[curDay] || entry.MinuteOfDay != curMinute)
                {
                    continue;
                }

                if (string.Equals(entry.Action, "Start", StringComparison.OrdinalIgnoreCase))
                {
                    rule.IsEnabled = true;
                }
                else if (string.Equals(entry.Action, "Stop", StringComparison.OrdinalIgnoreCase))
                {
                    rule.IsEnabled = false;
                }
                else if (string.Equals(entry.Action, "Reset", StringComparison.OrdinalIgnoreCase))
                {
                    rule.CurrentUsageIn = 0;
                    rule.CurrentUsageOut = 0;
                }
            }
        }
    }
}

