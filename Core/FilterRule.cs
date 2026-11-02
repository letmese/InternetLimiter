using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Valve.Core;

public enum FilterType
{
    Normal = 0,
    Filter = 0,
    Zone = 1,
    Composite = 2
}

public enum FilterOperator
{
    Is = 0,
    IsNot = 1,
    InRange = 2,
    NotInRange = 3,
    Contains = 4,
    DoesNotContain = 5
}

public enum FilterFunctionType
{
    Application = 0,
    ApplicationPath = 1,
    ApplicationPathContains = 2,
    Loopback = 3,
    LocalAddress = 4,
    RemoteAddress = 5,
    LocalPort = 6,
    RemotePort = 7,
    Network = 8,
    RemoteHostInternet = 9,
    RemoteHostLocal = 10,
    Protocol = 11,
    User = 12,
    Tag = 13,
    Forwarded = 14,
    DomainName = 15
}

public enum FilterCriterion
{
    ApplicationName = 0,
    ApplicationPath = 1,
    ApplicationPathContains = 2,
    Loopback = 3,
    LocalAddress = 4,
    RemoteAddress = 5,
    LocalPort = 6,
    RemotePort = 7,
    Network = 8,
    RemoteHostInternet = 9,
    RemoteHostLocal = 10,
    Protocol = 11,
    User = 12,
    Tag = 13,
    Forwarded = 14,
    DomainName = 15
}

public enum FilterConditionType
{
    Application = 0,
    ApplicationPath = 1,
    ApplicationPathContains = 2,
    Loopback = 3,
    LocalAddress = 4,
    RemoteAddress = 5,
    IpRange = 5,
    LocalPort = 6,
    RemotePort = 7,
    PortRange = 7,
    Network = 8,
    RemoteHostInternet = 9,
    RemoteHostLocal = 10,
    Zone = 10,
    Protocol = 11,
    User = 12,
    Tag = 13,
    Forwarded = 14,
    DomainName = 15
}

/// <summary>
/// Bulletproof domain wildcard matcher supporting apex domains (e.g. *.domain.com matches domain.com),
/// glob wildcards (* and ?), safe regex character escaping, and concurrent regex caching (Item 5.05.p).
/// </summary>
public static class DomainWildcardMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool Matches(string domain, string patternList)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(patternList))
        {
            return false;
        }

        domain = domain.Trim().TrimEnd('.');

        var patterns = patternList.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawPattern in patterns)
        {
            var p = rawPattern.Trim();
            if (string.IsNullOrEmpty(p)) continue;

            if (MatchSinglePattern(domain, p))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchSinglePattern(string domain, string pattern)
    {
        pattern = pattern.TrimEnd('.');

        // Special handling for *.domain.tld to match apex domain.tld directly
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            string apex = pattern[2..];
            if (domain.Equals(apex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Substring match fallback for non-wildcard patterns (preserves backward compatibility)
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return domain.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var regex = _regexCache.GetOrAdd(pattern, CreateRegexForGlob);
            return regex.IsMatch(domain);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Regex CreateRegexForGlob(string globPattern)
    {
        // 1. Regex.Escape escapes all regex metacharacters safely (+, (, ), [, ], {, }, ^, $, \, |, etc.)
        string escaped = Regex.Escape(globPattern);

        // 2. Transform escaped wildcards: \* -> .*, \? -> .
        string pattern;
        if (escaped.StartsWith(@"\*\.", StringComparison.Ordinal))
        {
            string rest = escaped[4..].Replace(@"\*", ".*").Replace(@"\?", ".");
            pattern = @"^(?:.*\.)?" + rest + @"$";
        }
        else
        {
            pattern = "^" + escaped.Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        }

        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
    }
}

/// <summary>
/// Bitmask-accurate IP address and CIDR / range matcher supporting IPv4, IPv6, loopback (127.0.0.0/8 and ::1),
/// RFC1918/RFC4193 private ranges, and hyphenated ranges (Items 5.05.d, 5.05.e, 5.05.f, 5.05.j, 5.05.k).
/// </summary>
public static class IpRangeMatcher
{
    public static bool IsLoopback(string ipStr)
    {
        if (string.IsNullOrWhiteSpace(ipStr)) return false;
        if (ipStr.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (ipStr.StartsWith("127.")) return true; // 127.0.0.0/8
        if (ipStr == "::1") return true;
        return IPAddress.TryParse(ipStr.Trim(), out var ip) && IPAddress.IsLoopback(ip);
    }

    public static bool IsPrivate(string ipStr)
    {
        if (string.IsNullOrWhiteSpace(ipStr) || !IPAddress.TryParse(ipStr.Trim(), out var ip))
        {
            return false;
        }

        byte[] bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && (bytes[1] >= 16 && bytes[1] <= 31)) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (Link Local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 127.0.0.0/8
            if (bytes[0] == 127) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1
            if (ip.Equals(IPAddress.IPv6Loopback)) return true;
            // fe80::/10 (Link Local)
            if (ip.IsIPv6LinkLocal) return true;
            // fc00::/7 (Unique Local Address)
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        return false;
    }

    public static bool Matches(string ipStr, string rangeExpression)
    {
        if (string.IsNullOrWhiteSpace(ipStr) || string.IsNullOrWhiteSpace(rangeExpression))
        {
            return false;
        }

        if (!IPAddress.TryParse(ipStr.Trim(), out var targetIp))
        {
            return false;
        }

        var tokens = rangeExpression.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var t = token.Trim();
            if (string.IsNullOrEmpty(t)) continue;

            if (MatchSingleRange(targetIp, t))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchSingleRange(IPAddress targetIp, string pattern)
    {
        if (pattern.EndsWith("."))
        {
            return targetIp.ToString().StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.Contains('/'))
        {
            var parts = pattern.Split('/');
            if (parts.Length == 2 && IPAddress.TryParse(parts[0].Trim(), out var networkIp) && int.TryParse(parts[1].Trim(), out int prefixLength))
            {
                if (targetIp.AddressFamily != networkIp.AddressFamily) return false;
                return IsInCidr(targetIp, networkIp, prefixLength);
            }
            return false;
        }

        if (pattern.Contains('-'))
        {
            var parts = pattern.Split('-');
            if (parts.Length == 2 && IPAddress.TryParse(parts[0].Trim(), out var startIp) && IPAddress.TryParse(parts[1].Trim(), out var endIp))
            {
                if (targetIp.AddressFamily != startIp.AddressFamily || targetIp.AddressFamily != endIp.AddressFamily)
                {
                    return false;
                }
                return IsInRange(targetIp, startIp, endIp);
            }
            return false;
        }

        if (IPAddress.TryParse(pattern, out var singleIp))
        {
            return targetIp.Equals(singleIp);
        }

        return targetIp.ToString().StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInCidr(IPAddress address, IPAddress network, int prefixLength)
    {
        byte[] addrBytes = address.GetAddressBytes();
        byte[] netBytes = network.GetAddressBytes();
        int maxPrefix = addrBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > maxPrefix) return false;

        int fullBytes = prefixLength / 8;
        int remBits = prefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (addrBytes[i] != netBytes[i]) return false;
        }

        if (remBits > 0)
        {
            byte mask = (byte)(0xFF << (8 - remBits));
            if ((addrBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask)) return false;
        }

        return true;
    }

    private static bool IsInRange(IPAddress address, IPAddress start, IPAddress end)
    {
        byte[] addrBytes = address.GetAddressBytes();
        byte[] startBytes = start.GetAddressBytes();
        byte[] endBytes = end.GetAddressBytes();

        for (int i = 0; i < addrBytes.Length; i++)
        {
            if (addrBytes[i] < startBytes[i]) return false;
            if (addrBytes[i] > startBytes[i]) break;
        }

        for (int i = 0; i < addrBytes.Length; i++)
        {
            if (addrBytes[i] > endBytes[i]) return false;
            if (addrBytes[i] < endBytes[i]) break;
        }

        return true;
    }
}

/// <summary>
/// Port and port range matcher supporting single ports, comma-separated lists, and hyphenated ranges (Items 5.05.g, 5.05.h).
/// </summary>
public static class PortRangeMatcher
{
    public static bool Matches(ushort port, string portExpression)
    {
        if (string.IsNullOrWhiteSpace(portExpression)) return false;

        var tokens = portExpression.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var t = token.Trim();
            if (string.IsNullOrEmpty(t)) continue;

            if (t.Contains('-'))
            {
                var parts = t.Split('-');
                if (parts.Length == 2 &&
                    ushort.TryParse(parts[0].Trim(), out ushort start) &&
                    ushort.TryParse(parts[1].Trim(), out ushort end))
                {
                    if (start > end) (start, end) = (end, start);
                    if (port >= start && port <= end) return true;
                }
            }
            else
            {
                if (ushort.TryParse(t, out ushort single))
                {
                    if (port == single) return true;
                }
            }
        }

        return false;
    }
}

/// <summary>
/// Unified connection and traffic flow context capturing all metadata for traffic selection evaluation.
/// </summary>
public class ConnectionContext
{
    public string AppName { get; set; } = string.Empty;
    public string AppPath { get; set; } = string.Empty;
    public int Pid { get; set; }
    public string LocalIp { get; set; } = string.Empty;
    public ushort LocalPort { get; set; }
    public string RemoteIp { get; set; } = string.Empty;
    public ushort RemotePort { get; set; }
    public string Protocol { get; set; } = "TCP";
    public string Domain { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string UserSid { get; set; } = string.Empty;
    public string NetworkAdapter { get; set; } = string.Empty;
    public string AdapterId { get; set; } = string.Empty;
    public bool IsLocal { get; set; }
    public bool IsLoopback { get; set; }
    public bool IsForwarded { get; set; }
    public bool IsInbound { get; set; }

    public ConnectionContext() { }

    public ConnectionContext(string appName, string appPath, string localIp, ushort localPort, string remoteIp, ushort remotePort, string protocol, string domain, string tags, bool isLocal)
    {
        AppName = appName;
        AppPath = appPath;
        LocalIp = localIp;
        LocalPort = localPort;
        RemoteIp = remoteIp;
        RemotePort = remotePort;
        Protocol = protocol;
        Domain = domain;
        Tags = tags;
        IsLocal = isLocal;
        IsLoopback = IpRangeMatcher.IsLoopback(localIp) || IpRangeMatcher.IsLoopback(remoteIp);
    }

    public ConnectionContext(ConnRow conn, ProcessRow? process = null)
    {
        if (conn != null)
        {
            Pid = conn.Pid;
            LocalIp = conn.LocalIp;
            LocalPort = (ushort)conn.LocalPort;
            RemoteIp = conn.RemoteIp;
            RemotePort = (ushort)conn.RemotePort;
            Protocol = conn.Proto;
            Domain = conn.Hostname;
            IsInbound = conn.IsInbound;
            IsLocal = IpRangeMatcher.IsPrivate(RemoteIp) || IpRangeMatcher.IsLoopback(RemoteIp);
            IsLoopback = IpRangeMatcher.IsLoopback(LocalIp) || IpRangeMatcher.IsLoopback(RemoteIp);
        }
        if (process != null)
        {
            AppName = process.Name;
            AppPath = process.Path;
            Tags = process.Tags;
        }
    }
}

public sealed class TrafficFlowContext : ConnectionContext
{
    public string NetworkName { get => NetworkAdapter; set => NetworkAdapter = value; }
    public string Username { get => UserName; set => UserName = value; }
}

/// <summary>
/// A single filter criterion function. Multiple values within ONE function evaluate as logical OR (Item 5.04).
/// </summary>
public sealed class FilterFunction : Observable
{
    private FilterCriterion _criterion = FilterCriterion.ApplicationName;
    private FilterOperator _operator = FilterOperator.Is;
    private bool _isNot;
    private string _value = string.Empty;

    public FilterFunction() { }

    public FilterFunction(FilterFunctionType functionType, FilterOperator op, string value)
    {
        _criterion = (FilterCriterion)functionType;
        _operator = op;
        _isNot = (op == FilterOperator.IsNot || op == FilterOperator.NotInRange || op == FilterOperator.DoesNotContain);
        _value = value;
    }

    public FilterFunction(FilterCriterion criterion, bool isNot, string value)
    {
        _criterion = criterion;
        _isNot = isNot;
        _operator = isNot ? FilterOperator.IsNot : FilterOperator.Is;
        _value = value;
    }

    public FilterCriterion Criterion
    {
        get => _criterion;
        set
        {
            if (Set(ref _criterion, value))
            {
                Raise(nameof(FunctionType));
                Raise(nameof(DisplayText));
            }
        }
    }

    [JsonIgnore]
    public FilterFunctionType FunctionType
    {
        get => (FilterFunctionType)_criterion;
        set => Criterion = (FilterCriterion)value;
    }

    public FilterOperator Operator
    {
        get => _operator;
        set
        {
            if (Set(ref _operator, value))
            {
                _isNot = (value == FilterOperator.IsNot || value == FilterOperator.NotInRange || value == FilterOperator.DoesNotContain);
                Raise(nameof(IsNot));
                Raise(nameof(DisplayText));
            }
        }
    }

    public bool IsNot
    {
        get => _isNot;
        set
        {
            if (Set(ref _isNot, value))
            {
                _operator = value ? FilterOperator.IsNot : FilterOperator.Is;
                Raise(nameof(Operator));
                Raise(nameof(DisplayText));
            }
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
            {
                Raise(nameof(RawValues));
                Raise(nameof(DisplayText));
            }
        }
    }

    [JsonIgnore]
    public string RawValues
    {
        get => _value;
        set => Value = value;
    }

    [JsonIgnore]
    public string DisplayText => $"{_criterion} {(_isNot ? "is not" : "is")} {_value}";

    public bool Matches(ConnectionContext context)
    {
        if (context == null) return false;

        bool matched = EvaluateCore(context);
        return _isNot ? !matched : matched;
    }

    private bool EvaluateCore(ConnectionContext context)
    {
        switch (_criterion)
        {
            case FilterCriterion.ApplicationName:
                return MatchTokens(context.AppName, _value, (val, tok) =>
                    val.Equals(tok, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(context.AppPath).Equals(tok, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(context.AppPath).Equals(tok, StringComparison.OrdinalIgnoreCase));

            case FilterCriterion.ApplicationPath:
                return MatchTokens(context.AppPath, _value, (val, tok) =>
                    val.Trim().Equals(tok.Trim(), StringComparison.OrdinalIgnoreCase));

            case FilterCriterion.ApplicationPathContains:
                return MatchTokens(context.AppPath, _value, (val, tok) =>
                    val.Contains(tok.Trim(), StringComparison.OrdinalIgnoreCase));

            case FilterCriterion.Loopback:
                return context.IsLoopback ||
                       IpRangeMatcher.IsLoopback(context.RemoteIp) ||
                       IpRangeMatcher.IsLoopback(context.LocalIp);

            case FilterCriterion.LocalAddress:
                return IpRangeMatcher.Matches(context.LocalIp, _value);

            case FilterCriterion.RemoteAddress:
                return IpRangeMatcher.Matches(context.RemoteIp, _value);

            case FilterCriterion.LocalPort:
                return PortRangeMatcher.Matches(context.LocalPort, _value);

            case FilterCriterion.RemotePort:
                return PortRangeMatcher.Matches(context.RemotePort, _value);

            case FilterCriterion.Network:
                return MatchTokens(context.NetworkAdapter, _value, (val, tok) =>
                    val.Equals(tok.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    context.AdapterId.Equals(tok.Trim(), StringComparison.OrdinalIgnoreCase));

            case FilterCriterion.RemoteHostInternet:
                return !context.IsLocal && !context.IsLoopback && !IpRangeMatcher.IsPrivate(context.RemoteIp) && !IpRangeMatcher.IsLoopback(context.RemoteIp);

            case FilterCriterion.RemoteHostLocal:
                return context.IsLocal || IpRangeMatcher.IsPrivate(context.RemoteIp) || IpRangeMatcher.IsLoopback(context.RemoteIp);

            case FilterCriterion.Protocol:
                return MatchTokens(context.Protocol, _value, (val, tok) =>
                    val.Equals(tok.Trim(), StringComparison.OrdinalIgnoreCase));

            case FilterCriterion.User:
                return MatchTokens(context.UserName, _value, (val, tok) =>
                    val.Equals(tok.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    context.UserSid.Equals(tok.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    val.EndsWith("\\" + tok.Trim(), StringComparison.OrdinalIgnoreCase));

            case FilterCriterion.Tag:
                return MatchTokens(context.Tags, _value, (val, tok) =>
                    val.Contains(tok.Trim(), StringComparison.OrdinalIgnoreCase));

            case FilterCriterion.Forwarded:
                return context.IsForwarded;

            case FilterCriterion.DomainName:
                return DomainWildcardMatcher.Matches(context.Domain, _value);

            default:
                return false;
        }
    }

    private static bool MatchTokens(string actual, string expression, Func<string, string, bool> predicate)
    {
        if (string.IsNullOrEmpty(actual) || string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var tokens = expression.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
        {
            var trimmed = t.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (predicate(actual, trimmed))
            {
                return true; // Logical OR
            }
        }

        return false;
    }
}

/// <summary>
/// High-performance Filter model supporting 16 criteria, boolean AND across functions, boolean OR within values,
/// and Filter types (Normal, Zone, Composite) (Items 5.01 to 5.09).
/// </summary>
public class FilterRule : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private FilterType _filterType = FilterType.Normal;
    private FilterConditionType _conditionType = FilterConditionType.ApplicationPath;
    private string _value = string.Empty;
    private int _downloadLimitKbps;
    private int _uploadLimitKbps;
    private bool _downloadLimitEnabled;
    private bool _uploadLimitEnabled;
    private LimitApplicationType _limitType = LimitApplicationType.PerApplication;
    private bool _blocked;
    private TrafficPriority _priority = TrafficPriority.Normal;
    private bool _isEnabled = true;

    public FilterRule() { }

    public FilterRule(string name, FilterConditionType conditionType, string value)
    {
        _name = name;
        _conditionType = conditionType;
        _value = value;
    }

    public string Id
    {
        get => _id;
        set => Set(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public FilterType FilterType
    {
        get => _filterType;
        set => Set(ref _filterType, value);
    }

    [JsonIgnore]
    public FilterType Type
    {
        get => _filterType;
        set => FilterType = value;
    }

    public ObservableCollection<FilterFunction> Functions { get; set; } = new();

    public List<string> CompositeChildIds { get; set; } = new();

    [JsonIgnore]
    public List<string> CompositeFilterIds
    {
        get => CompositeChildIds;
        set => CompositeChildIds = value;
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    public FilterConditionType ConditionType
    {
        get => _conditionType;
        set
        {
            if (Set(ref _conditionType, value))
            {
                Raise(nameof(ConditionTypeText));
            }
        }
    }

    [JsonIgnore]
    public string ConditionTypeText => _conditionType switch
    {
        FilterConditionType.Application => "Application is",
        FilterConditionType.ApplicationPath => "Application path is",
        FilterConditionType.ApplicationPathContains => "Application path contains",
        FilterConditionType.Loopback => "Loopback traffic (127.0.0.0/8 & ::1)",
        FilterConditionType.LocalAddress => "Local IP / Subnet in range",
        FilterConditionType.RemoteAddress => "Remote IP / Subnet in range",
        FilterConditionType.LocalPort => "Local port in range",
        FilterConditionType.RemotePort => "Remote port in range",
        FilterConditionType.Network => "Network adapter is",
        FilterConditionType.RemoteHostInternet => "Remote host is on Internet",
        FilterConditionType.RemoteHostLocal => "Remote host is on Local Network",
        FilterConditionType.Protocol => "Protocol is",
        FilterConditionType.User => "Windows User account / SID is",
        FilterConditionType.Tag => "App tag is",
        FilterConditionType.Forwarded => "Is forwarded traffic",
        FilterConditionType.DomainName => "Domain name (e.g. *.youtube.com)",
        _ => "Custom",
    };

    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    public int DownloadLimitKbps
    {
        get => _downloadLimitKbps;
        set
        {
            if (Set(ref _downloadLimitKbps, value))
            {
                Raise(nameof(DownloadLimitText));
            }
        }
    }

    [JsonIgnore]
    public string DownloadLimitText => _downloadLimitKbps > 0
        ? (_downloadLimitEnabled ? $"{_downloadLimitKbps} kbit/s" : $"{_downloadLimitKbps} kbit/s (off)")
        : "unlimited";

    public bool DownloadLimitEnabled
    {
        get => _downloadLimitEnabled;
        set
        {
            if (Set(ref _downloadLimitEnabled, value))
            {
                Raise(nameof(EffectiveDownloadLimitKbps));
                Raise(nameof(DownloadLimitText));
            }
        }
    }

    public int UploadLimitKbps
    {
        get => _uploadLimitKbps;
        set
        {
            if (Set(ref _uploadLimitKbps, value))
            {
                Raise(nameof(UploadLimitText));
            }
        }
    }

    [JsonIgnore]
    public string UploadLimitText => _uploadLimitKbps > 0
        ? (_uploadLimitEnabled ? $"{_uploadLimitKbps} kbit/s" : $"{_uploadLimitKbps} kbit/s (off)")
        : "unlimited";

    public bool UploadLimitEnabled
    {
        get => _uploadLimitEnabled;
        set
        {
            if (Set(ref _uploadLimitEnabled, value))
            {
                Raise(nameof(EffectiveUploadLimitKbps));
                Raise(nameof(UploadLimitText));
            }
        }
    }

    public LimitApplicationType LimitType
    {
        get => _limitType;
        set => Set(ref _limitType, value);
    }

    [JsonIgnore]
    public int EffectiveDownloadLimitKbps => _downloadLimitEnabled ? _downloadLimitKbps : 0;

    [JsonIgnore]
    public int EffectiveUploadLimitKbps => _uploadLimitEnabled ? _uploadLimitKbps : 0;

    public bool Blocked
    {
        get => _blocked;
        set => Set(ref _blocked, value);
    }

    public TrafficPriority Priority
    {
        get => _priority;
        set => Set(ref _priority, value);
    }

    public bool Matches(ConnectionContext context, IReadOnlyDictionary<string, FilterRule>? filterLookup = null)
    {
        if (context == null) return false;

        // Composite Filter evaluation (Item 5.03)
        if (_filterType == FilterType.Composite && filterLookup != null && CompositeChildIds.Count > 0)
        {
            return CompositeChildIds.Any(id => filterLookup.TryGetValue(id, out var child) && child.Matches(context, filterLookup));
        }

        // Multi-function evaluation with logical AND across functions (Item 5.04)
        if (Functions.Count > 0)
        {
            foreach (var func in Functions)
            {
                if (!func.Matches(context)) return false;
            }
            return true;
        }

        // Legacy single-condition evaluation for 100% backward compatibility
        return MatchesLegacy(context);
    }

    private bool MatchesLegacy(ConnectionContext context)
    {
        switch (_conditionType)
        {
            case FilterConditionType.Application:
                return !string.IsNullOrEmpty(context.AppName) && context.AppName.Equals(_value, StringComparison.OrdinalIgnoreCase);

            case FilterConditionType.ApplicationPath:
                return !string.IsNullOrEmpty(context.AppPath) && context.AppPath.Contains(_value, StringComparison.OrdinalIgnoreCase);

            case FilterConditionType.ApplicationPathContains:
                return !string.IsNullOrEmpty(context.AppPath) && context.AppPath.Contains(_value, StringComparison.OrdinalIgnoreCase);

            case FilterConditionType.IpRange:
                return IpRangeMatcher.Matches(context.RemoteIp, _value);

            case FilterConditionType.PortRange:
                return PortRangeMatcher.Matches(context.RemotePort, _value);

            case FilterConditionType.Protocol:
                return context.Protocol.Equals(_value, StringComparison.OrdinalIgnoreCase);

            case FilterConditionType.DomainName:
                return DomainWildcardMatcher.Matches(context.Domain, _value);

            case FilterConditionType.Zone:
                if (_value.Equals("Local", StringComparison.OrdinalIgnoreCase)) return context.IsLocal;
                return !context.IsLocal;

            case FilterConditionType.Tag:
                return !string.IsNullOrEmpty(context.Tags) && context.Tags.Contains(_value, StringComparison.OrdinalIgnoreCase);

            case FilterConditionType.Loopback:
                return context.IsLoopback || IpRangeMatcher.IsLoopback(context.RemoteIp) || IpRangeMatcher.IsLoopback(context.LocalIp);

            default:
                return false;
        }
    }

    public bool Matches(string appPath, string remoteIp, ushort remotePort, string proto, string domain, string tags, bool isLocal)
    {
        string appName = string.IsNullOrEmpty(appPath) ? "" : Path.GetFileName(appPath);
        var ctx = new ConnectionContext(appName, appPath, "", 0, remoteIp, remotePort, proto, domain, tags, isLocal);
        return Matches(ctx);
    }
}

/// <summary>
/// Alias / subclass of FilterRule for declarative composite and multi-function filter definitions.
/// </summary>
public sealed class FilterDefinition : FilterRule
{
    public FilterDefinition() { }

    public FilterDefinition(string name)
    {
        Name = name;
    }
}
