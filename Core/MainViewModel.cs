using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Valve.Views;

namespace Valve.Core;

/// <summary>One configured application in the rules list.</summary>
public sealed class RuleRow : Observable
{
    private long _usedIn;
    private long _usedOut;
    private double _quotaMbEdit;
    private int _periodDaysEdit = 1;

    public RuleRow(AppRule rule)
    {
        Rule = rule;
        _quotaMbEdit = Math.Round(rule.QuotaBytes / 1048576.0, 1);
        _periodDaysEdit = rule.QuotaDays;
    }

    public AppRule Rule { get; }
    public string App => Rule.App;
    public string AppPath => Rule.AppPath;
    public int LimitKbps => Rule.UploadLimitKbps;

    public bool Blocked => Rule.Blocked;

    public double QuotaMbEdit { get => _quotaMbEdit; set => Set(ref _quotaMbEdit, value); }
    public int PeriodDaysEdit { get => _periodDaysEdit; set => Set(ref _periodDaysEdit, value); }

    public long UsedToday => _usedIn + _usedOut;
    public string UsedText => Fmt.Bytes(_usedIn + _usedOut);

    public string LimitText => Rule.UploadLimitKbps > 0 ? $"{Rule.UploadLimitKbps} kbit/s" : "unlimited";
    public string DownloadLimitText => Rule.DownloadLimitKbps > 0 ? $"{Rule.DownloadLimitKbps} kbit/s" : "unlimited";
    public string PriorityText => Rule.Priority.ToString().ToLowerInvariant();
    public string QuotaText => Rule.QuotaBytes > 0
        ? $"{Fmt.Bytes((long)(_usedIn + _usedOut))} / {Fmt.Bytes(Rule.QuotaBytes)}"
        : "none";

    public double QuotaFraction => Policy.QuotaFraction(Rule, _usedIn, _usedOut);
    public bool OverQuota => Policy.QuotaExceeded(Rule, _usedIn, _usedOut);

    public void SetUsage(long usedIn, long usedOut)
    {
        _usedIn = usedIn;
        _usedOut = usedOut;
        Raise(nameof(QuotaText));
        Raise(nameof(QuotaFraction));
        Raise(nameof(OverQuota));
        Raise(nameof(UsedText));
        Raise(nameof(UsedToday));
    }

    public void Refresh()
    {
        Raise(nameof(LimitKbps));
        Raise(nameof(LimitText));
        Raise(nameof(Blocked));
        Raise(nameof(PriorityText));
        Raise(nameof(QuotaText));
    }
}

/// <summary>One time-windowed cap in the schedule list.</summary>
public sealed class ScheduleRow : Observable
{
    private bool _active;

    public ScheduleRow(ScheduleRule rule) => Rule = rule;

    public ScheduleRule Rule { get; }
    public string App => Rule.App;
    public string Window => Rule.Window;
    public string LimitText => $"{Rule.LimitKbps} kbit/s";

    public string DaysText
    {
        get
        {
            bool[] d = Rule.Days;
            if (d.All(x => x))
            {
                return "every day";
            }

            if (d[1] && d[2] && d[3] && d[4] && d[5] && !d[0] && !d[6])
            {
                return "weekdays";
            }

            if (!d[1] && !d[2] && !d[3] && !d[4] && !d[5] && d[0] && d[6])
            {
                return "weekends";
            }

            string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            return string.Join(" ", Enumerable.Range(0, 7).Where(i => d[i]).Select(i => names[i]));
        }
    }

    public bool IsActiveNow
    {
        get => _active;
        set => Set(ref _active, value);
    }

    public void Refresh()
    {
        IsActiveNow = Policy.IsActive(Rule, DateTime.Now);
        Raise(nameof(DaysText));
        Raise(nameof(Window));
        Raise(nameof(LimitText));
    }
}

public enum ProcessFilter
{
    All,
    Downloading,
    Uploading,
    Blocked,
    Capped,
}

/// <summary>One entry in the left navigation panel.</summary>
public sealed class FilterItem : Observable
{
    private int _count;

    public FilterItem(string label, ProcessFilter mode)
    {
        Label = label;
        Mode = mode;
    }

    public string Label { get; }
    public ProcessFilter Mode { get; }
    public int Count { get => _count; set { if (Set(ref _count, value)) { Raise(nameof(CountText)); } } }
    public string CountText => _count == 0 ? "" : _count.ToString();
}

/// <summary>One row in the long-term statistics list.</summary>
public sealed class StatsRow
{
    public StatsRow(string app, long bytesIn, long bytesOut)
    {
        App = app;
        BytesIn = bytesIn;
        BytesOut = bytesOut;
    }

    public string App { get; }
    public long BytesIn { get; }
    public long BytesOut { get; }
    public string InText => Fmt.Bytes(BytesIn);
    public string OutText => Fmt.Bytes(BytesOut);
    public string TotalText => Fmt.Bytes(BytesIn + BytesOut);
}

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Collector _collector;
    private readonly IShaper _shaper;
    private readonly IFirewall _firewall;
    private readonly HistoryStore _history;
    private readonly SettingsStore _store;
    private readonly Settings _settings;
    private readonly GeoIp _geo;
    private readonly DriverClient _driver = new();
    private readonly WinDivertShaper _divertShaper = new();
    private readonly Dictionary<int, (ulong In, ulong Out)> _driverPrev = new();
    private DateTime _driverPrevAt = DateTime.UtcNow;
    private ulong _prevTotalIn;
    private ulong _prevTotalOut;
    private bool _firstTotalSample = true;

    /// <summary>Indicates whether the UI is connected to an active headless Valve background service (Items 28.01, 28.02).</summary>
    public bool IsConnectedToService { get; private set; }

    private readonly Dictionary<int, ProcessRow> _byPid = new();
    private readonly Dictionary<string, ConnRow> _byConn = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GraphSample> _graph = new();
    private readonly DnsResolver _dns = new();
    private readonly string _appsPath;
    private readonly Dictionary<string, ApplicationEntry> _appRegistry = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ApplicationEntry> Applications { get; } = new();
    public event Action<IReadOnlyList<GraphSample>>? NodeGraphChanged;

    private double _totalIn;
    private double _totalOut;
    private string? _note;
    private string _status = "starting";
    private int _tick;
    private bool _disposed;

    private long _sessionTotalInBytes;
    private long _sessionTotalOutBytes;
    private long _sessionInternetInBytes;
    private long _sessionInternetOutBytes;
    private long _sessionLocalInBytes;
    private long _sessionLocalOutBytes;

    public long SessionTotalInBytes => _sessionTotalInBytes;
    public long SessionTotalOutBytes => _sessionTotalOutBytes;
    public long SessionInternetInBytes => _sessionInternetInBytes;
    public long SessionInternetOutBytes => _sessionInternetOutBytes;
    public long SessionLocalInBytes => _sessionLocalInBytes;
    public long SessionLocalOutBytes => _sessionLocalOutBytes;

    public MainViewModel(
        Collector collector,
        IShaper shaper,
        IFirewall firewall,
        HistoryStore history,
        SettingsStore store,
        GeoIp geo)
    {
        _ui = Dispatcher.CurrentDispatcher;
        _collector = collector;
        _shaper = shaper;
        _firewall = firewall;
        _history = history;
        _remoteIpHistory = new RemoteIpHistoryStore();
        _store = store;
        _geo = geo;
        _settings = store.Load();
        // Master toggles must ALWAYS start OFF by default when the app starts per user requirement
        _settings.BlockerEnabled = false;
        _settings.LimiterEnabled = false;
        _settings.PrioritiesEnabled = false;
        _settings.GlobalDownloadLimitEnabled = false;
        _settings.GlobalUploadLimitEnabled = false;
        _settings.GlobalDownloadLimitKbps = 0;
        _settings.GlobalUploadLimitKbps = 0;

        Filters.Add(new FilterItem("All processes", ProcessFilter.All));
        Filters.Add(new FilterItem("Downloading", ProcessFilter.Downloading));
        Filters.Add(new FilterItem("Uploading", ProcessFilter.Uploading));
        Filters.Add(new FilterItem("Capped", ProcessFilter.Capped));
        Filters.Add(new FilterItem("Blocked", ProcessFilter.Blocked));
        CurrentFilter = Filters[0];

        foreach (AppRule rule in _settings.Apps)
        {
            Rules.Add(new RuleRow(rule));
        }

        if (_settings.Rules == null) _settings.Rules = new List<UnifiedRule>();
        foreach (UnifiedRule rule in _settings.Rules)
        {
            UnifiedRules.Add(rule);
        }

        foreach (ScheduleRule schedule in _settings.Schedules)
        {
            Schedules.Add(new ScheduleRow(schedule));
        }

        if (_settings.Filters.Count == 0)
        {
            _settings.Filters.Add(new FilterRule("Web Browsing (HTTP/HTTPS)", FilterConditionType.PortRange, "80, 443"));
            _settings.Filters.Add(new FilterRule("DNS Traffic", FilterConditionType.PortRange, "53"));
            _settings.Filters.Add(new FilterRule("Local Subnet", FilterConditionType.IpRange, "192.168.0.0/16"));
            _store.Save(_settings);
        }

        foreach (FilterRule f in _settings.Filters)
        {
            FilterRules.Add(f);
        }
        RefreshNetworkAdapters();

        _collector.Sampled += OnSampled;
        _geo.Resolved += OnGeoResolved;
        _appsPath = System.IO.Path.Combine(_store.RootDirectory, "apps.json");
        LoadApplications();
        _dns.Resolved += OnDnsResolved;
        _dns.Start();
        _divertShaper.DnsResolver = _dns;
        _divertShaper.IsBlockerEnabled = _settings.BlockerEnabled;
        _divertShaper.DefaultBlockerAction = _settings.DefaultBlockerAction;
        ThemeManager.ApplyFontScale(_settings.FontScale);
        VersionChecker.UpdateUrl = _settings.UpdateCheckUrl;
        _controlAllowed = WindowsAclManager.EvaluateIdentity(_settings).CanControl && (Valve.Interop.Native.IsUserAnAdmin() || WindowsAclManager.IsAdministrator());
        _divertShaper.OnPacketHeld += OnPacketHeldByShaper;
        _divertShaper.SetUnifiedRules(_settings.Rules);
        _divertShaper.SetFilters(_settings.Filters);
        ApplySettings();

        IsConnectedToService = ValveIpcClient.IsServiceRunning();
        if (IsConnectedToService)
        {
            try
            {
                string? statusJson = ValveIpcClient.SendCommand("STATUS", timeoutMs: 500);
                if (!string.IsNullOrEmpty(statusJson))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(statusJson);
                    if (doc.RootElement.TryGetProperty("shaperAvailable", out var sa))
                    {
                        _serviceShaperAvailable = sa.GetBoolean();
                    }
                }
                // Synchronize clean initial GUI state to service immediately (B1)
                ValveIpcClient.SendCommand("SYNC_RULES", System.Text.Json.JsonSerializer.Serialize(_settings));
            }
            catch { }
            Report($"Connected to background Valve service (Pipe: ValveServicePipe) — {(_serviceShaperAvailable ? "managed by service" : "service shaper offline")}");
            StartServiceMonitoringTimer();
        }
        else
        {
            _divertShaper.Start();
            if (_divertShaper.UnavailableReason == "driver files missing")
            {
                Note = "Packet engine unavailable: WinDivert driver files missing — monitoring only";
            }
        }

        SyncTagFilters();
        _ui.BeginInvoke(() =>
        {
            try
            {
                CheckDbHealth();
            }
            catch (Exception)
            {
                // Health check must never block startup.
            }
        });
    }

    public sealed record AppStoreDto(
        string Path,
        string Name,
        DateTime FirstSeen,
        DateTime LastSeen,
        long TotalBytesIn,
        long TotalBytesOut,
        string? Tags,
        bool IsHidden);

    private void LoadApplications()
    {
        try
        {
            if (File.Exists(_appsPath))
            {
                string? json = AtomicStorage.LoadAtomic(_appsPath, out _);
                if (!string.IsNullOrEmpty(json))
                {
                    var saved = System.Text.Json.JsonSerializer.Deserialize<List<AppStoreDto>>(json);
                    if (saved != null)
                    {
                        foreach (var s in saved)
                        {
                            var entry = new ApplicationEntry(s.Path, s.Name)
                            {
                                FirstSeen = s.FirstSeen,
                                LastSeen = s.LastSeen,
                                TotalBytesIn = s.TotalBytesIn,
                                TotalBytesOut = s.TotalBytesOut,
                                Tags = s.Tags ?? "",
                                IsHidden = s.IsHidden
                            };
                            _appRegistry[s.Path] = entry;
                            Applications.Add(entry);
                        }
                    }
                }
            }
        }
        catch { }
    }

    public void TrackApplication(string path, string name, long bytesIn, long bytesOut)
    {
        if (string.IsNullOrEmpty(path)) return;

        if (!_appRegistry.TryGetValue(path, out var entry))
        {
            entry = new ApplicationEntry(path, name)
            {
                FirstSeen = DateTime.Now,
                LastSeen = DateTime.Now,
                TotalBytesIn = Math.Max(0, bytesIn),
                TotalBytesOut = Math.Max(0, bytesOut),
            };
            _appRegistry[path] = entry;
            _ui.BeginInvoke(() => Applications.Add(entry));
            EnsureAutoFilter(path, name);
            SaveApplications();
        }
        else
        {
            entry.LastSeen = DateTime.Now;
            if (bytesIn > 0) entry.TotalBytesIn += bytesIn;
            if (bytesOut > 0) entry.TotalBytesOut += bytesOut;
        }
    }

    private readonly HashSet<string> _autoFilterPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// NetLimiter-style automatic per-application filters (Item 5.06).
    /// Every newly observed application gets a filter node matching its path.
    /// Bounded to 200 auto filters to keep the list manageable.
    /// </summary>
    private void EnsureAutoFilter(string path, string name)
    {
        try
        {
            if (_autoFilterPaths.Count >= 200 || string.IsNullOrEmpty(path)) return;
            if (!_autoFilterPaths.Add(path)) return;

            var existing = _settings.Filters.FirstOrDefault(f =>
                f.FilterType == FilterType.Normal &&
                f.Functions.Count > 0 &&
                f.Functions[0].Criterion == FilterCriterion.ApplicationPathContains &&
                string.Equals(f.Functions[0].Value, path, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                _autoFilterPaths.Add(path);
                return;
            }

            string filterName = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name;
            var filter = new FilterRule
            {
                Name = $"{filterName} (auto)",
                FilterType = FilterType.Normal,
                ConditionType = FilterConditionType.ApplicationPathContains,
                Value = path,
            };
            filter.Functions.Add(new FilterFunction(FilterFunctionType.ApplicationPathContains, FilterOperator.Contains, path));

            _ui.BeginInvoke(() =>
            {
                FilterRules.Add(filter);
                _settings.Filters.Add(filter);
                _store.Save(_settings);
            });
        }
        catch { }
    }

    public void SaveApplications()
    {
        try
        {
            var dtos = Applications.Select(a => new AppStoreDto(
                a.Path, a.Name, a.FirstSeen, a.LastSeen, a.TotalBytesIn, a.TotalBytesOut, a.Tags, a.IsHidden)).ToList();
            string json = System.Text.Json.JsonSerializer.Serialize(dtos);
            AtomicStorage.SaveAtomic(_appsPath, json);
        }
        catch { }
    }

    public int CleanStaleApplications()
    {
        var stale = Applications.Where(a => !File.Exists(a.Path)).ToList();
        foreach (var s in stale)
        {
            Applications.Remove(s);
            _appRegistry.Remove(s.Path);
        }
        if (stale.Count > 0)
        {
            SaveApplications();
            Report($"Pruned {stale.Count} stale applications from catalog.");
        }
        else
        {
            Report("No stale applications found.");
        }
        return stale.Count;
    }

    private void OnDnsResolved(string address, string hostname)
    {
        _ui.BeginInvoke(() =>
        {
            foreach (ConnRow row in Connections)
            {
                if (string.Equals(row.RemoteIp, address, StringComparison.OrdinalIgnoreCase))
                {
                    row.Hostname = hostname;
                }
            }
            if (SelectedConnection != null && string.Equals(SelectedConnection.RemoteIp, address, StringComparison.OrdinalIgnoreCase))
            {
                Raise(nameof(ConnectionHostname));
            }
        });
    }

    private void OnGeoResolved(string address, GeoLocation location)
    {
        _ui.BeginInvoke(() =>
        {
            foreach (ConnRow row in Connections)
            {
                if (string.Equals(row.RemoteIp, address, StringComparison.OrdinalIgnoreCase))
                {
                    row.Location = location.DisplayWithFlag;
                }
            }
            if (SelectedConnection != null && string.Equals(SelectedConnection.RemoteIp, address, StringComparison.OrdinalIgnoreCase))
            {
                Raise(nameof(ConnectionLocationWithFlag));
                Raise(nameof(ConnectionLocation));
            }
        });
    }

    public ObservableCollection<ProcessRow> Processes { get; } = new();
    public ObservableCollection<TreeNode> TreeRows { get; } = new();
    private readonly List<TreeNode> _treeOrder = new();
    public ObservableCollection<ConnRow> Connections { get; } = new();
    public ObservableCollection<RuleRow> Rules { get; } = new();
    public ObservableCollection<UnifiedRule> UnifiedRules { get; } = new();
    public ObservableCollection<UnifiedRule> SelectedItemRules { get; } = new();
    public ObservableCollection<ScheduleRow> Schedules { get; } = new();
    public ObservableCollection<StatsRow> Weekly { get; } = new();
    public ObservableCollection<FilterItem> Filters { get; } = new();
    public ObservableCollection<NetworkAdapterRow> NetworkAdapters { get; } = new();
    public ObservableCollection<FilterRule> FilterRules { get; } = new();
    public ObservableCollection<LogEntry> ConnectionLogs { get; } = new();
    public ObservableCollection<EntityStatsRow> IpStats { get; } = new();
    public ObservableCollection<EntityStatsRow> PortStats { get; } = new();
    private readonly Dictionary<string, EntityStatsRow> _ipStatsMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EntityStatsRow> _portStatsMap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Real pending allow/deny queue surfaced by the Pending Requests window (3.04 Ask).</summary>
    public ObservableCollection<PendingRequestItem> PendingRequests { get; } = new();

    public const string ControlPermissionRequiredMessage = "Control permission required (run elevated or ask an administrator)";
    private bool _controlAllowed = true;
    public bool CanControl
    {
        get => _controlAllowed;
        set => _controlAllowed = value;
    }

    public bool CheckControlPermission()
    {
        if (!_controlAllowed)
        {
            Report(ControlPermissionRequiredMessage);
            return false;
        }
        return true;
    }

    public bool ToggleLimiter()
    {
        if (!CheckControlPermission()) return false;
        _settings.LimiterEnabled = !_settings.LimiterEnabled;
        ApplySettings();
        return true;
    }

    public bool TogglePriorities()
    {
        if (!CheckControlPermission()) return false;
        _settings.PrioritiesEnabled = !_settings.PrioritiesEnabled;
        ApplySettings();
        return true;
    }

    public bool ToggleBlocker()
    {
        if (!CheckControlPermission()) return false;
        _settings.BlockerEnabled = !_settings.BlockerEnabled;
        ApplySettings();
        return true;
    }

    public bool SetBlockerAction(string action)
    {
        if (!CheckControlPermission()) return false;
        _settings.DefaultBlockerAction = action;
        ApplySettings();
        return true;
    }

    public bool SetBlockerAction(BlockerAction action) => SetBlockerAction(action.ToString());

    public bool SetGlobalLimits(int dlKbps, int ulKbps, bool dlEnabled, bool ulEnabled)
    {
        if (!CheckControlPermission()) return false;
        _settings.GlobalDownloadLimitKbps = dlKbps;
        _settings.GlobalDownloadLimitEnabled = dlEnabled;
        _settings.GlobalUploadLimitKbps = ulKbps;
        _settings.GlobalUploadLimitEnabled = ulEnabled;
        ApplySettings();
        return true;
    }

    public void AddFilterRule(FilterRule rule)
    {
        if (!CheckControlPermission()) return;
        FilterRules.Add(rule);
        SaveFilters();
    }

    public void RemoveFilterRule(FilterRule rule)
    {
        if (!CheckControlPermission()) return;
        FilterRules.Remove(rule);
        SaveFilters();
    }
    private readonly HashSet<string> _pendingSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly RemoteIpHistoryStore _remoteIpHistory;

    private string? _vtVerdict;
    public string? VtVerdict { get => _vtVerdict; private set => Set(ref _vtVerdict, value); }
    public bool VtChecked { get; private set; }
    public string VtVerdictText => VtChecked
        ? (string.IsNullOrEmpty(VtVerdict) ? "No VirusTotal API key configured." : VtVerdict)
        : "Not checked.";

    private FilterItem _currentFilter = null!;
    public FilterItem CurrentFilter
    {
        get => _currentFilter;
        set
        {
            if (Set(ref _currentFilter, value))
            {
                FilterChanged?.Invoke();
            }
        }
    }

    private TreeNode? _selectedNode;
    public TreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value))
            {
                return;
            }

            SelectedProcess = value?.Process;
            Raise(nameof(HasSelection));
            Raise(nameof(SelectedSha256));
            Raise(nameof(IsConnectionSelected));
            Raise(nameof(IsProcessSelected));
            Raise(nameof(SelectedConnection));
            Raise(nameof(InfoCategory));
            Raise(nameof(SelectedName));
            Raise(nameof(SelectedPath));
            Raise(nameof(SelectedSubtitle));
            Raise(nameof(ConnectionProto));
            Raise(nameof(ConnectionLocal));
            Raise(nameof(ConnectionRemote));
            Raise(nameof(ConnectionLocation));
            Raise(nameof(ConnectionLocationWithFlag));
            Raise(nameof(ConnectionDirectionBadge));
            Raise(nameof(ConnectionHostname));
            Raise(nameof(ConnectionRttText));
            Raise(nameof(ConnectionRetransText));
            Raise(nameof(ConnectionCardVisibility));
            Raise(nameof(ProcessCardVisibility));
            Raise(nameof(MachineCardVisibility));
            Raise(nameof(SelectedProduct));
            Raise(nameof(SelectedVersion));
            Raise(nameof(SelectedCompany));
            Raise(nameof(SelectedDescription));
            Raise(nameof(SelectedServiceName));
            Raise(nameof(SelectedServiceDisplayName));
            Raise(nameof(HasServiceInfo));
            Raise(nameof(ConnectionProcessName));
            Raise(nameof(KillButtonText));
            Raise(nameof(SelectedInRateText));
            Raise(nameof(SelectedOutRateText));
            RaiseRuleState();
            RefreshSelectedItemRules();

            if (SelectedConnection != null)
            {
                NodeGraphChanged?.Invoke(SelectedConnection.GraphHistory);
            }
            else if (SelectedProcess != null)
            {
                NodeGraphChanged?.Invoke(SelectedProcess.GraphHistory);
            }
            else
            {
                lock (_graph)
                {
                    NodeGraphChanged?.Invoke(_graph.ToList());
                }
            }
        }
    }

    public string SelectedInRateText => _selectedNode is not null ? _selectedNode.InRateText : TotalInText;
    public string SelectedOutRateText => _selectedNode is not null ? _selectedNode.OutRateText : TotalOutText;

    public ConnRow? SelectedConnection => _selectedNode?.Connection;
    public bool IsConnectionSelected => _selectedNode?.Connection is not null;
    public bool IsProcessSelected => !IsConnectionSelected;

    public string InfoCategory
    {
        get
        {
            if (IsConnectionSelected) return "Connection";
            if (SelectedProcess is not null) return $"Process {SelectedProcess.Pid}";
            if (_selectedNode is not null) return _selectedNode.Name;
            return "Info View";
        }
    }

    public string ConnectionProto => SelectedConnection?.Proto ?? "";
    public string ConnectionLocal => SelectedConnection?.Local ?? "";
    public string ConnectionRemote => SelectedConnection?.Remote ?? "";
    public string ConnectionLocation => SelectedConnection?.Location ?? "";
    public string ConnectionProcessName => SelectedProcess is not null ? $"{SelectedProcess.Name} (PID {SelectedProcess.Pid})" : "";
    public string ConnectionDirectionBadge => SelectedConnection?.DirectionBadge ?? "";
    public string ConnectionLocationWithFlag => SelectedConnection?.Location ?? "";
    public string ConnectionHostname => SelectedConnection?.Hostname ?? "";
    public string ConnectionRttText => SelectedConnection?.RttText ?? "-";
    public string ConnectionRetransText => SelectedConnection?.RetransText ?? "-";

    public string SelectedProduct => SelectedProcess?.ProductName ?? "";
    public string SelectedVersion => SelectedProcess?.ProductVersion ?? "";
    public string AppVersion => "v" + Valve.Api.ValveApi.Version;
    public string SelectedCompany => SelectedProcess?.CompanyName ?? "";
    public string SelectedDescription => SelectedProcess?.FileDescription ?? "";
    public string SelectedServiceName => SelectedProcess?.ServiceName ?? "";
    public string SelectedServiceDisplayName => SelectedProcess?.ServiceDisplayName ?? "";
    public bool HasServiceInfo => !string.IsNullOrEmpty(SelectedServiceName);

    public System.Windows.Visibility ConnectionCardVisibility => IsConnectionSelected ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility ProcessCardVisibility => IsProcessSelected ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility MachineCardVisibility => (!IsConnectionSelected && !IsProcessSelected) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string KillButtonText => IsConnectionSelected ? "Kill connection" : "Kill connections";

    private void RaiseRuleState()
    {
        Raise(nameof(RuleBlockerIn));
        Raise(nameof(RuleBlockerOut));
        Raise(nameof(RuleLimitIn));
        Raise(nameof(RuleLimitOut));
        Raise(nameof(RulePriorityIn));
        Raise(nameof(RulePriorityOut));
        Raise(nameof(RuleQuotaIn));
        Raise(nameof(RuleQuotaOut));
        Raise(nameof(FirewallInStateText));
        Raise(nameof(FirewallOutStateText));
    }

    private static string BlockerStateText(BlockerState s) => s switch
    {
        BlockerState.Deny => "Deny",
        BlockerState.Allow => "Allow",
        _ => "Not set",
    };

    public string RuleBlockerIn => SelectedProcess is null ? "Not set" : BlockerStateText(FindRule(SelectedProcess.Name)?.BlockerIn ?? BlockerState.NotSet);
    public string RuleBlockerOut => SelectedProcess is null ? "Not set" : BlockerStateText(FindRule(SelectedProcess.Name)?.BlockerOut ?? BlockerState.NotSet);
    public string RuleLimitIn => SelectedProcess?.HasLimit == true ? $"{SelectedProcess.LimitKbps} kbit/s" : "Not set";

    /// <summary>Firewall panel: per-direction state of the selected app (replaces the old static ALLOW card).</summary>
    public string FirewallInStateText => SelectedProcess is null ? "— NOT SET" : BlockerStateText(FindRule(SelectedProcess.Name)?.BlockerIn ?? BlockerState.NotSet).ToUpperInvariant();
    public string FirewallOutStateText => SelectedProcess is null ? "— NOT SET" : BlockerStateText(FindRule(SelectedProcess.Name)?.BlockerOut ?? BlockerState.NotSet).ToUpperInvariant();

    /// <summary>Ask/Allow toggle for new remote endpoints (Item 3.09).</summary>
    public bool AskOnNewEndpoint
    {
        get => _settings.DefaultBlockerAction.Equals("Ask", StringComparison.OrdinalIgnoreCase);
        set
        {
            _settings.DefaultBlockerAction = value ? "Ask" : "Allow";
            Persist();
            Raise(nameof(AskOnNewEndpoint));
        }
    }

    /// <summary>Download limits are the one thing user-mode cannot do.</summary>
    public string RuleLimitOut => "Unavailable";
    public string RulePriorityIn => SelectedProcess is null ? "Not set" : SelectedProcess.PriorityText;
    public string RulePriorityOut => "Not set";
    public string RuleQuotaIn => "Not set";
    public string RuleQuotaOut => "Not set";

    public string SelectedName => _selectedNode?.Name ?? "";
    public string SelectedPath => _selectedNode?.Path ?? "";
    public string SelectedSubtitle => _selectedNode?.Subtitle ?? "";

    public string SelectedSha256
    {
        get
        {
            string path = SelectedPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    using var stream = File.OpenRead(path);
                    return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                }
                catch { }
            }
            return "-";
        }
    }

    public string SelectedTags
    {
        get
        {
            if (SelectedProcess is null) return "";
            string exe = SelectedProcess.Name.ToLowerInvariant();
            var rule = _settings.Apps.FirstOrDefault(a => a.App.Equals(exe, StringComparison.OrdinalIgnoreCase));
            return rule?.Tags ?? "";
        }
    }

    public void SetAppTags(string appName, string tags)
    {
        var rule = EnsureRule(appName, SelectedPath);
        rule.Tags = tags;
        if (SelectedProcess != null) SelectedProcess.Tags = tags;
        Persist();
        SyncTagFilters();
        Raise(nameof(SelectedTags));
    }

    public bool IsTrafficIgnored(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string exe = System.IO.Path.GetFileName(path).ToLowerInvariant();
        var rule = _settings.Apps.FirstOrDefault(a => a.App.Equals(exe, StringComparison.OrdinalIgnoreCase));
        return rule?.IgnoreTraffic == true;
    }

    public void AddConnectionLog(string app, string eventType, string proto, string local, string remote, string location, string ruleAction)
    {
        try
        {
            App.Current?.Dispatcher?.InvokeAsync(() =>
            {
                if (ConnectionLogs.Count >= 2000)
                {
                    ConnectionLogs.RemoveAt(0);
                }
                ConnectionLogs.Add(new LogEntry
                {
                    App = app,
                    Event = eventType,
                    Proto = proto,
                    Local = local,
                    Remote = remote,
                    Location = location,
                    RuleAction = ruleAction,
                });
            });
        }
        catch { }
    }

    public void ExportConnectionsToCsv(string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Process,PID,Protocol,Local,Remote,Location,InRate,OutRate");
        foreach (var c in Connections)
        {
            sb.AppendLine($"\"{c.Process}\",{c.Pid},\"{c.Proto}\",\"{c.Local}\",\"{c.Remote}\",\"{c.Location}\",\"{c.InRateText}\",\"{c.OutRateText}\"");
        }
        File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
    }

    public void ExportStatsToCsv(string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Application,InBytes,OutBytes,TotalBytes,InFormatted,OutFormatted");
        foreach (var s in Weekly)
        {
            sb.AppendLine($"\"{s.App}\",{s.BytesIn},{s.BytesOut},{s.BytesIn + s.BytesOut},\"{s.InText}\",\"{s.OutText}\"");
        }
        File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
    }

    public void ExportLogsToCsv(string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Timestamp,Application,Event,Protocol,Local,Remote,Location,RuleAction");
        foreach (var l in ConnectionLogs)
        {
            sb.AppendLine($"\"{l.TimeText}\",\"{l.App}\",\"{l.Event}\",\"{l.Proto}\",\"{l.Local}\",\"{l.Remote}\",\"{l.Location}\",\"{l.RuleAction}\"");
        }
        File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
    }

    public void ResetTotals()
    {
        foreach (var p in Processes)
        {
            p.ResetTotals();
        }
        foreach (var c in Connections)
        {
            c.InTotal = 0;
            c.OutTotal = 0;
        }
        Report("All session transfer totals have been reset.");
    }

    public int CleanStaleRules()
    {
        int countBefore = _settings.Apps.Count;
        _settings.Apps.RemoveAll(a => !string.IsNullOrEmpty(a.AppPath) && !File.Exists(a.AppPath));
        int removed = countBefore - _settings.Apps.Count;
        if (removed > 0)
        {
            Persist();
            Rules.Clear();
            foreach (var r in _settings.Apps)
            {
                Rules.Add(new RuleRow(r));
            }
            Report($"Cleaned up {removed} stale application rule(s).");
        }
        else
        {
            Report("No stale application rules found.");
        }
        return removed;
    }

    public void RepairHistoryDb()
    {
        _history.RepairDatabase();
        Report("Database integrity check and repair completed.");
    }

    public void ExportDiagnostics(string destPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== VALVE SYSTEM DIAGNOSTICS REPORT ===");
        sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"Machine: {Environment.MachineName}");
        sb.AppendLine($"Active Processes: {Processes.Count}");
        sb.AppendLine($"Active Connections: {Connections.Count}");
        sb.AppendLine($"Configured Rules: {Rules.Count}");
        sb.AppendLine($"Connection Logs Count: {ConnectionLogs.Count}");
        sb.AppendLine($"Limiter Enabled: {_settings.LimiterEnabled}");
        sb.AppendLine($"Blocker Enabled: {_settings.BlockerEnabled}");
        sb.AppendLine($"Emergency Block Active: {EmergencyBlockActive}");
        sb.AppendLine();
        sb.AppendLine("=== ACTIVE RULES ===");
        foreach (var r in Rules)
        {
            sb.AppendLine($"App: {r.App} | Path: {r.AppPath} | Blocked: {r.Blocked} | UL Limit: {r.LimitKbps} | DL Limit: {r.DownloadLimitText} | Priority: {r.PriorityText}");
        }
        sb.AppendLine();
        sb.AppendLine("=== RECENT CONNECTION LOGS (Last 50) ===");
        foreach (var l in ConnectionLogs.TakeLast(50))
        {
            sb.AppendLine($"[{l.TimeText}] {l.App} - {l.Event} - {l.Proto} - {l.Local} -> {l.Remote} ({l.Location}) - Action: {l.RuleAction}");
        }
        File.WriteAllText(destPath, sb.ToString(), System.Text.Encoding.UTF8);
    }

    public int AllCount => Processes.Count;
    public int OnlineCount => Processes.Count(p => p.IsOnline);
    public int OfflineCount => Processes.Count(p => !p.IsOnline && !p.IsBlocked);
    public int BlockedCount => Processes.Count(p => p.IsBlocked);

    public string AllCountText => $"All ({AllCount})";
    public string OnlineCountText => $"Online ({OnlineCount})";
    public string OfflineCountText => $"Offline ({OfflineCount})";
    public string BlockedCountText => $"Blocked ({BlockedCount})";
    public int HiddenCount => Processes.Count(p => p.IsHidden);
    public string HiddenCountText => $"Hidden ({HiddenCount})";

    public bool DualYAxis
    {
        get => _settings.DualYAxis;
        set { _settings.DualYAxis = value; Persist(); Raise(nameof(DualYAxis)); }
    }

    public void HideProcess(ProcessRow row)
    {
        row.IsHidden = true;
        _settings.HiddenApps.Add(row.Name);
        Persist();
        Raise(nameof(HiddenCountText));
        Report($"Moved {row.Name} to Hidden");
    }

    public void UnhideProcess(ProcessRow row)
    {
        row.IsHidden = false;
        _settings.HiddenApps.Remove(row.Name);
        Persist();
        Raise(nameof(HiddenCountText));
        Report($"Unhid {row.Name}");
    }

    public void SetConnectionLimit(ConnRow conn, int kbps)
    {
        conn.LimitKbps = kbps;
        _settings.ConnectionLimits[conn.Key] = kbps;
        Persist();
        Report($"Connection {conn.Remote} limit set to {(kbps > 0 ? $"{kbps} kbit/s" : "unlimited")}");
    }

    public void ToggleFullPath()
    {
        Fmt.ShowFullPath = !Fmt.ShowFullPath;
        _settings.ShowFullPath = Fmt.ShowFullPath;
        Persist();
        foreach (var p in Processes)
        {
            p.RefreshText();
        }
        Report($"Full paths: {(Fmt.ShowFullPath ? "ON" : "OFF")}");
    }

    public void SetStatsPeriod(int days)
    {
        IReadOnlyList<DailyUsage> data;
        string label;
        if (days == -1)
        {
            // 26.06: "This month" — stats since the configured first day of the month.
            int firstDay = Math.Clamp(_settings.FirstDayOfMonth, 1, 28);
            DateOnly today = DateOnly.FromDateTime(DateTime.Now);
            DateOnly start = new DateOnly(today.Year, today.Month, firstDay);
            if (start > today)
            {
                start = start.AddMonths(-1);
            }
            data = _history.GetRangeSince(start);
            label = $"This month (from day {firstDay})";
        }
        else
        {
            data = _history.GetRange(days);
            label = days == 0 ? "All Time" : $"Last {days} days";
        }

        Weekly.Clear();
        foreach (DailyUsage usage in data)
        {
            Weekly.Add(new StatsRow(usage.App, usage.BytesIn, usage.BytesOut));
        }
        Report($"Statistics updated for {label}");
    }

    private double _peakRate = 1048576;
    public string PeakRateText => $"Peak: {Fmt.Bytes((long)_peakRate)}/s";

    public string LineSpeedText
    {
        get
        {
            var detected = _divertShaper.GetDetectedLineSpeed();
            long dl = _settings.AutoDetectLineSpeed && detected.InBps > 0 ? detected.InBps : _settings.GlobalDownloadLineSpeedBps;
            long ul = _settings.AutoDetectLineSpeed && detected.OutBps > 0 ? detected.OutBps : _settings.GlobalUploadLineSpeedBps;
            return $"Line: ↓ {UnitConverter.FormatString(dl, _settings.UseBits)}  ↑ {UnitConverter.FormatString(ul, _settings.UseBits)}";
        }
    }

    private bool _emergencyBlock;
    public bool EmergencyBlockActive
    {
        get => _emergencyBlock;
        set
        {
            if (Set(ref _emergencyBlock, value))
            {
                Raise(nameof(EmergencyBlockText));
                if (value)
                {
                    _ = _firewall.SetEmergencyBlockAsync(true);
                    Collector.KillConnections(-1);
                    Report("EMERGENCY BLOCK ACTIVATED - All network traffic blocked");
                }
                else
                {
                    _ = _firewall.SetEmergencyBlockAsync(false);
                    _ = SyncFirewallAsync();
                    Report("Emergency block deactivated");
                }
            }
        }
    }
    public string EmergencyBlockText => _emergencyBlock ? "EMERGENCY BLOCK: ON" : "Emergency Block";

    public NetworkAdapterRow? ActiveAdapter
    {
        get
        {
            if (!string.IsNullOrEmpty(_settings.SelectedAdapterId))
            {
                var match = NetworkAdapters.FirstOrDefault(a => a.Id == _settings.SelectedAdapterId || a.Name.Equals(_settings.SelectedAdapterId, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            return NetworkAdapters
                .Where(a => a.Status.Equals("Up", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.Ip) && a.Ip != "-" && !a.Ip.StartsWith("127."))
                .OrderByDescending(a => GetAdapterScore(a))
                .FirstOrDefault() ?? NetworkAdapters.FirstOrDefault();
        }
    }

    private static int GetAdapterScore(NetworkAdapterRow a)
    {
        int score = 0;
        string name = (a.Name ?? "").ToLowerInvariant();
        string desc = (a.Description ?? "").ToLowerInvariant();
        string type = (a.Type ?? "").ToLowerInvariant();

        bool hasGateway = !string.IsNullOrEmpty(a.Gateway) && a.Gateway != "-";
        if (hasGateway) score += 100;

        bool isVirtual = name.Contains("vethernet") || name.Contains("wsl") || name.Contains("virtual") ||
                         name.Contains("hyper-v") || name.Contains("vmware") || name.Contains("virtualbox") ||
                         name.Contains("tap") || name.Contains("npcap") || name.Contains("loopback") ||
                         name.Contains("bluetooth") || name.Contains("tailscale") || name.Contains("zerotier") ||
                         desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("vmware") ||
                         desc.Contains("tap") || desc.Contains("wsl");

        if (isVirtual) score -= 150;

        if (type.Contains("wireless") || name.Contains("wi-fi") || name.Contains("wifi") || desc.Contains("wi-fi") || desc.Contains("wireless"))
            score += 50;
        else if (type.Contains("ethernet") && !isVirtual)
            score += 60;

        return score;
    }

    public string ActiveAdapterSummary
    {
        get
        {
            var active = ActiveAdapter;
            if (active != null)
            {
                string gateway = string.IsNullOrEmpty(active.Gateway) ? "-" : active.Gateway;
                return $"Adapter: {active.Name} | IP: {active.Ip} | Gateway: {gateway}";
            }
            return "Adapter: Scanning...";
        }
    }

    public void SelectAdapter(string adapterId)
    {
        _settings.SelectedAdapterId = adapterId ?? "";
        Persist();
        Raise(nameof(ActiveAdapter));
        Raise(nameof(ActiveAdapterSummary));
    }

    public void ExpandAll()
    {
        foreach (var node in _treeNodes.Values)
        {
            if (node.CanExpand) node.IsExpanded = true;
        }
        RefreshTree();
    }

    public void CollapseAll()
    {
        foreach (var node in _treeNodes.Values)
        {
            if (node.Level == 0)
                node.IsExpanded = true;
            else if (node.CanExpand)
                node.IsExpanded = false;
        }
        RefreshTree();
    }

    private ProcessRow? _selectedProcess;
    public ProcessRow? SelectedProcess
    {
        get => _selectedProcess;
        set { if (Set(ref _selectedProcess, value)) { Raise(nameof(HasSelection)); } }
    }

    public bool HasSelection => _selectedProcess is not null;

    public event Action? FilterChanged;

    public IReadOnlyList<GraphSample> Graph => _graph;

    public double TotalIn { get => _totalIn; private set { if (Set(ref _totalIn, value)) { Raise(nameof(TotalInText)); Raise(nameof(SelectedInRateText)); } } }
    public double TotalOut { get => _totalOut; private set { if (Set(ref _totalOut, value)) { Raise(nameof(TotalOutText)); Raise(nameof(SelectedOutRateText)); } } }
    public string TotalInText => Fmt.Rate(_totalIn);
    public string TotalOutText => Fmt.Rate(_totalOut);

    public string? Note { get => _note; set => Set(ref _note, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool Elevated => _collector.Elevated;
    public bool FirewallAvailable => _firewall.IsAvailable;
    public bool ShaperAvailable => _shaper.IsAvailable;
    public bool DivertAvailable => IsConnectedToService ? _serviceShaperAvailable : _divertShaper.IsAvailable;
    public string DivertUnavailableReason
    {
        get
        {
            if (IsConnectedToService)
            {
                if (!_serviceShaperAvailable)
                {
                    int hbSec = (int)(_serviceHeartbeatMs / 1000.0);
                    string reason = string.IsNullOrEmpty(_serviceShaperReason) ? "dead" : _serviceShaperReason;
                    return $"service shaper dead ({reason}, heartbeat {hbSec}s)";
                }
                return "managed by service";
            }
            return _divertShaper?.UnavailableReason ?? "uninitialized";
        }
    }
    public double EngineHeartbeatAgeMs => IsConnectedToService ? _serviceHeartbeatMs : (_divertShaper != null ? (DateTime.UtcNow - _divertShaper.LastPacketUtc).TotalMilliseconds : -1);
    public double Pid0Ratio => IsConnectedToService ? _servicePid0Ratio : (_divertShaper?.Pid0Ratio ?? 0.0);
    public double TryGetStatsHitRatio => _divertShaper?.TryGetStatsHitRatio ?? 0.0;
    public bool ServiceShaperAvailable => _serviceShaperAvailable;
    public double ServiceHeartbeatAgeMs => _serviceHeartbeatMs;
    public string? ServiceShaperReason => _serviceShaperReason;

    public string EnvironmentText
    {
        get
        {
            var parts = new List<string>();
            parts.Add(_collector.Elevated ? "elevated" : "not elevated");
            parts.Add(_firewall.IsAvailable ? "firewall ready" : $"firewall off ({_firewall.UnavailableReason})");
            parts.Add(_shaper.IsAvailable ? "qos ready" : $"qos off ({_shaper.UnavailableReason})");
            if (IsConnectedToService)
            {
                if (_serviceShaperAvailable)
                {
                    parts.Add("managed by service");
                }
                else
                {
                    int hbSec = (int)(_serviceHeartbeatMs / 1000.0);
                    parts.Add($"service shaper dead (heartbeat {hbSec}s)");
                }
            }
            else
            {
                if (!_divertShaper.IsAvailable)
                {
                    parts.Add("shaper standby");
                }
                else if (_divertShaper.IsLimiterEnabled && _divertShaper.HasActiveLimits)
                {
                    parts.Add("download limiter active");
                }
                else
                {
                    parts.Add("driver ready (no limits active)");
                }
            }
            return string.Join("  ·  ", parts);
        }
    }

    private System.Threading.Timer? _serviceMonitorTimer;
    private int _servicePingFailures = 0;
    private readonly object _servicePerPidLock = new();
    private readonly Dictionary<int, (ulong In, ulong Out)> _servicePerPidStats = new();
    private ulong _serviceTotalIn;
    private ulong _serviceTotalOut;
    private double _serviceHeartbeatMs = -1;
    private double _servicePid0Ratio;
    private bool _serviceShaperAvailable = false;
    private string? _serviceShaperReason;

    private void StartServiceMonitoringTimer()
    {
        _serviceMonitorTimer?.Dispose();
        _serviceMonitorTimer = new System.Threading.Timer(_ =>
        {
            if (!IsConnectedToService) return;
            try
            {
                string? statsJson = ValveIpcClient.SendCommand("STATS", timeoutMs: 800);
                if (!string.IsNullOrEmpty(statsJson))
                {
                    var stats = System.Text.Json.JsonSerializer.Deserialize<WinDivertShaper.ServiceStatsPayload>(statsJson);
                    if (stats != null)
                    {
                        _serviceTotalIn = stats.totalIn;
                        _serviceTotalOut = stats.totalOut;
                        _serviceHeartbeatMs = stats.heartbeatMs;
                        _servicePid0Ratio = stats.pid0Ratio;
                        _serviceShaperAvailable = stats.shaperAvailable;
                        _serviceShaperReason = stats.shaperReason;
                        if (!_serviceShaperAvailable)
                        {
                            _servicePingFailures++;
                        }
                        else
                        {
                            _servicePingFailures = 0;
                        }
                        lock (_servicePerPidLock)
                        {
                            _servicePerPidStats.Clear();
                            foreach (var p in stats.perPid)
                            {
                                _servicePerPidStats[p.pid] = (p.@in, p.@out);
                            }
                        }
                        if (_servicePingFailures >= 3)
                        {
                            _serviceMonitorTimer?.Dispose();
                            _serviceMonitorTimer = null;
                            IsConnectedToService = false;
                            _divertShaper.Start();
                            Report($"Valve service shaper offline ({_serviceShaperReason ?? "no driver"}): fell back to local shaper");
                            Raise(nameof(EnvironmentText));
                            return;
                        }
                    }
                }
                else
                {
                    _servicePingFailures++;
                    if (_servicePingFailures >= 3)
                    {
                        _serviceMonitorTimer?.Dispose();
                        _serviceMonitorTimer = null;
                        IsConnectedToService = false;
                        _divertShaper.Start();
                        Report("Valve service unreachable: fell back to local shaper");
                        Raise(nameof(EnvironmentText));
                        return;
                    }
                }

                // Poll HELD_PACKETS from service
                string? heldJson = ValveIpcClient.SendCommand("HELD_PACKETS", timeoutMs: 800);
                if (!string.IsNullOrEmpty(heldJson))
                {
                    var heldList = System.Text.Json.JsonSerializer.Deserialize<List<WinDivertShaper.HeldPacketDto>>(heldJson);
                    if (heldList != null && heldList.Count > 0)
                    {
                        _ui.BeginInvoke(() =>
                        {
                            foreach (var hp in heldList)
                            {
                                string app = string.IsNullOrEmpty(hp.processName) ? $"pid_{hp.pid}" : hp.processName.ToLowerInvariant();
                                string key = PendingKey(app, hp.remoteIp);
                                if (_pendingSeen.Add(key))
                                {
                                    var item = new PendingRequestItem
                                    {
                                        PacketId = hp.id,
                                        FlowKey = hp.flowKey,
                                        HeaderText = $"{(hp.isInbound ? "Inbound" : "Outgoing")} connection - {hp.protocol}",
                                        AppName = string.IsNullOrEmpty(hp.processName) ? $"Process {hp.pid}" : hp.processName,
                                        Path = hp.appPath,
                                        Pid = hp.pid,
                                        LocalEndpoint = $"{hp.localIp}:{hp.localPort}",
                                        RemoteEndpoint = $"{hp.remoteIp}:{hp.remotePort}",
                                        Icon = Icons.ForPath(hp.appPath)
                                    };
                                    PendingRequests.Add(item);
                                    ToastWindow.ShowToast("Blocker: Connection Request", $"{item.AppName} wants to connect to {item.RemoteEndpoint}", primaryLabel: "Review", isWarning: false);
                                    Report($"{item.AppName}: connection to {item.RemoteEndpoint} held awaiting your decision (Ask)");
                                }
                            }
                        });
                    }
                }
            }
            catch { }
        }, null, 500, 1000);
    }

    private void NotifyServiceAppLimit(string app, int kbps, bool isDownload, bool enabled)
    {
        if (IsConnectedToService)
        {
            try
            {
                string payload = System.Text.Json.JsonSerializer.Serialize(new AppLimitPayload
                {
                    app = app,
                    kbps = kbps,
                    isDownload = isDownload,
                    enabled = enabled
                });
                ValveIpcClient.SendCommand("SET_APP_LIMIT", payload);
            }
            catch { }
        }
    }

    public event Action? GraphUpdated;

    public void Report(string message) => Status = message;

    public AppRule? RuleFor(string app) => FindRule(app);

    /// <summary>
    /// Sets the download cap. This is the direction user-mode QoS could never
    /// touch - it only exists because the kernel driver drops packets.
    /// </summary>
    public Task SetDownloadCapAsync(ProcessRow row, int kbps)
    {
        if (!CheckControlPermission()) return Task.CompletedTask;
        AppRule rule = EnsureRule(row.Name, row.Path);
        rule.DownloadLimitKbps = kbps;
        rule.DownloadLimitEnabled = kbps > 0;
        row.DownloadLimitKbps = kbps;
        row.DownloadLimitEnabled = kbps > 0;

        _divertShaper.SetAppLimit(rule.App, kbps, isDownload: true, enabled: kbps > 0);
        if (!string.IsNullOrEmpty(rule.AppPath) && !string.Equals(rule.AppPath, rule.App, StringComparison.OrdinalIgnoreCase))
        {
            _divertShaper.SetAppLimit(rule.AppPath, kbps, isDownload: true, enabled: kbps > 0);
        }
        foreach (ProcessRow live in Processes)
        {
            if (string.Equals(live.Name, row.Name, StringComparison.OrdinalIgnoreCase))
            {
                live.DownloadLimitKbps = kbps;
                live.DownloadLimitEnabled = kbps > 0;
                if (live.Pid > 0)
                {
                    _divertShaper.SetProcessLimit(live.Pid, kbps, isDownload: true, enabled: kbps > 0);
                }
            }
        }
        NotifyServiceAppLimit(rule.App, kbps, isDownload: true, enabled: kbps > 0);

        SyncDriver();
        row.RefreshText();
        PersistSettings();

        Report(kbps > 0
            ? $"{row.Name}: download capped at {kbps} kbit/s"
            : $"{row.Name}: download cap cleared");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Pushes the current caps to the kernel driver and arms enforcement only if
    /// at least one cap exists. A loaded driver with no caps stays inert.
    /// </summary>
    private void SyncDriver()
    {
        var ruleMap = _settings.Apps.ToDictionary(r => r.App, StringComparer.OrdinalIgnoreCase);

        if (_divertShaper.IsAvailable)
        {
            foreach (ProcessRow live in Processes)
            {
                if (ruleMap.TryGetValue(live.Name, out AppRule? rule) && rule.DownloadLimitKbps > 0 && rule.DownloadLimitEnabled)
                {
                    _divertShaper.SetDownloadLimit(live.Pid, rule.DownloadLimitKbps);
                }
                else
                {
                    _divertShaper.SetProcessLimit(live.Pid, 0, isDownload: true, enabled: false);
                }
            }
        }

        if (!_driver.IsAvailable && !_driver.TryOpen())
        {
            return;
        }

        bool anyCap = false;

        foreach (ProcessRow live in Processes)
        {
            uint uploadBytesPerSecond = 0;
            uint downloadBytesPerSecond = 0;

            if (ruleMap.TryGetValue(live.Name, out AppRule? rule))
            {
                if (rule.UploadLimitEnabled && rule.UploadLimitKbps > 0)
                {
                    uploadBytesPerSecond = (uint)rule.UploadLimitKbps * 1000u / 8u;
                }
                if (rule.DownloadLimitEnabled && rule.DownloadLimitKbps > 0)
                {
                    downloadBytesPerSecond = (uint)rule.DownloadLimitKbps * 1000u / 8u;
                }
            }

            if (uploadBytesPerSecond > 0 || downloadBytesPerSecond > 0)
            {
                anyCap = true;
            }

            _driver.SetLimit(live.Pid, uploadBytesPerSecond, downloadBytesPerSecond);
        }

        _driver.SetEnabled(anyCap);
    }

    public void PersistSettings()
    {
        _store.Save(_settings);
        if (IsConnectedToService)
        {
            try
            {
                ValveIpcClient.SendCommand("SYNC_RULES", System.Text.Json.JsonSerializer.Serialize(_settings));
            }
            catch { }
        }
    }

    public void SaveFilters()
    {
        _settings.Filters.Clear();
        _settings.Filters.AddRange(FilterRules);
        _store.Save(_settings);
    }

    public void AddFilterFromConnection(ConnRow conn)
    {
        if (!CheckControlPermission()) return;
        var rule = new FilterRule($"Filter: {conn.RemoteIp}", FilterConditionType.IpRange, conn.RemoteIp);
        FilterRules.Add(rule);
        SaveFilters();
        Report($"Created filter rule for {conn.RemoteIp}.");
    }

    public void AddFilterFromProcess(ProcessRow proc)
    {
        if (!CheckControlPermission()) return;
        var rule = new FilterRule($"Filter: {proc.Name}", FilterConditionType.ApplicationPath, proc.Path);
        FilterRules.Add(rule);
        SaveFilters();
        Report($"Created filter rule for {proc.Name}.");
    }

    public void AddFilterFromProcesses(IEnumerable<ProcessRow> procs)
    {
        if (!CheckControlPermission()) return;
        var list = procs?.Distinct().ToList() ?? new List<ProcessRow>();
        if (list.Count == 0) return;

        if (list.Count == 1)
        {
            AddFilterFromProcess(list[0]);
            return;
        }

        string paths = string.Join(",", list.Select(p => p.Path).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct());
        var rule = new FilterRule($"Filter: {list.Count} apps", FilterConditionType.ApplicationPath, paths);
        FilterRules.Add(rule);
        SaveFilters();
        Report($"Created filter rule for {list.Count} applications.");
    }

    public Task RequestFirewallSyncAsync() => SyncFirewallAsync();

    public Settings CurrentSettings => _settings;

    public bool IsPaused => _collector.IsPaused;

    public void TogglePause()
    {
        if (_collector.IsPaused)
        {
            _collector.Resume();
            Status = "monitoring resumed";
        }
        else
        {
            _collector.Pause();
            Status = "monitoring paused";
        }

        Raise(nameof(IsPaused));
    }

    /// <summary>Pushes the current settings into the running components and saves them.</summary>
    public void ApplySettings()
    {
        _collector.IntervalSeconds = _settings.SampleIntervalSeconds;
        Fmt.UseBits = _settings.UseBits;
        _geo.Enabled = _settings.ResolveLocations;
        _history.RetentionDays = _settings.HistoryRetentionDays;
        UpdateStartupRegistry(_settings.StartWithWindows);

        // Propagate master switches and line speeds to active WinDivert shaper (Items 1.10, 2.05, 2.06)
        _divertShaper.IsLimiterEnabled = _settings.LimiterEnabled;
        _divertShaper.IsPrioritiesEnabled = _settings.PrioritiesEnabled;
        _divertShaper.IsBlockerEnabled = _settings.BlockerEnabled;
        _divertShaper.DefaultBlockerAction = _settings.DefaultBlockerAction;
        ThemeManager.ApplyFontScale(_settings.FontScale);
        VersionChecker.UpdateUrl = _settings.UpdateCheckUrl;
        _controlAllowed = WindowsAclManager.EvaluateIdentity(_settings).CanControl && (Valve.Interop.Native.IsUserAnAdmin() || WindowsAclManager.IsAdministrator());
        _divertShaper.GlobalDownloadLimitKbps = _settings.GlobalDownloadLimitEnabled ? _settings.GlobalDownloadLimitKbps : 0;
        _divertShaper.GlobalUploadLimitKbps = _settings.GlobalUploadLimitEnabled ? _settings.GlobalUploadLimitKbps : 0;
        _divertShaper.SetLineSpeed(_settings.GlobalDownloadLineSpeedBps, _settings.GlobalUploadLineSpeedBps);
        _divertShaper.ReactionSpeed = _settings.PriorityReactionSpeed;
        _divertShaper.AdaptiveAllocation = _settings.AdaptiveBandwidthAllocation;
        _divertShaper.UseReversedLimits = _settings.UseReversedLimitsPriority;
        _divertShaper.SetPriorityReservations(_settings.CriticalReservationPercent, _settings.HighReservationPercent, _settings.NormalReservationPercent, _settings.LowReservationPercent);

        // Synchronize app rules into WinDivertShaper
        foreach (AppRule rule in _settings.Apps)
        {
            _divertShaper.SetAppLimit(rule.App, rule.EffectiveDownloadLimitKbps, isDownload: true, enabled: rule.DownloadLimitEnabled);
            _divertShaper.SetAppLimit(rule.App, rule.EffectiveUploadLimitKbps, isDownload: false, enabled: rule.UploadLimitEnabled);
            _divertShaper.SetAppPriority(rule.App, rule.Priority);
        }

        // Synchronize filter rules into WinDivertShaper
        foreach (FilterRule filter in _settings.Filters)
        {
            _divertShaper.SetFilterLimit(filter.Name, filter.EffectiveDownloadLimitKbps, isDownload: true, enabled: filter.DownloadLimitEnabled);
            _divertShaper.SetFilterLimit(filter.Name, filter.EffectiveUploadLimitKbps, isDownload: false, enabled: filter.UploadLimitEnabled);
            _divertShaper.SetFilterPriority(filter.Name, filter.Priority);
        }

        // Synchronize connection rules into WinDivertShaper
        foreach (var (k, v) in _settings.ConnectionLimits)
        {
            _divertShaper.SetConnectionLimit(k, v, isDownload: false, enabled: v > 0);
        }
        foreach (var (k, v) in _settings.ConnectionDownloadLimits)
        {
            _divertShaper.SetConnectionLimit(k, v, isDownload: true, enabled: v > 0);
        }
        foreach (var (k, v) in _settings.ConnectionPriorities)
        {
            _divertShaper.SetConnectionPriority(k, v);
        }

        foreach (ProcessRow row in Processes)
        {
            row.RefreshText();
        }

        foreach (ConnRow row in Connections)
        {
            row.RefreshText();
        }

        foreach (RuleRow row in Rules)
        {
            row.Refresh();
        }

        double currentTotal = TotalIn + TotalOut;
        if (currentTotal > _peakRate)
        {
            _peakRate = currentTotal;
            Raise(nameof(PeakRateText));
        }
        Raise(nameof(LineSpeedText));
        GraphUpdated?.Invoke();
        PersistSettings();
        Status = "settings applied";
    }

    public (int DayCount, long TotalBytes) GetHistoryStats() => _history.GetStorageStats();

    /// <summary>Underlying persistent SQLite3 stats database path (Item 13.01).</summary>
    public string SqliteDbPath => _history.SqliteStore.DatabasePath;
    public SqliteHistoryStore HistorySqliteStore => _history.SqliteStore;

    /// <summary>Stats Manager: earliest/latest recorded dates.</summary>
    public (DateOnly? First, DateOnly? Last) GetHistoryDateRange() => _history.GetDateRange();

    /// <summary>Stats Manager: clears a date range of recorded history.</summary>
    public int ClearHistoryRange(DateOnly start, DateOnly end)
    {
        _history.ClearRange(start, end);
        RefreshHistoryUi();
        return 1;
    }

    /// <summary>Stats Manager: removes one app from all recorded days.</summary>
    public int DeleteAppHistory(string app)
    {
        _history.DeleteAppAll(app);
        RefreshHistoryUi();
        return 1;
    }

    /// <summary>Stats Manager: merges several apps into one target app.</summary>
    public int MergeAppHistory(string target, IReadOnlyList<string> sources)
    {
        _history.MergeApps(target, sources);
        RefreshHistoryUi();
        return 1;
    }

    /// <summary>Stats Manager: integrity check + auto repair.</summary>
    public int RepairHistory()
    {
        _history.RepairDatabase();
        return 1;
    }

    /// <summary>Stats Manager: imports day files from another Valve history folder.</summary>
    public int ImportHistory(string folder) => _history.ImportFromDirectory(folder);

    /// <summary>Stats Manager: per-app aggregated totals for the application list.</summary>
    public IReadOnlyList<DailyUsage> GetAllAppTotals() => _history.GetAllApps();

    /// <summary>Stats Manager: rewrites every day file through the atomic writer.</summary>
    public int CompactHistory() => _history.CompactAll();

    private void RefreshHistoryUi()
    {
        Raise(nameof(HistoryDayCount));
        Raise(nameof(HistoryTotalBytes));
    }

    public int HistoryDayCount => GetHistoryStats().DayCount;
    public long HistoryTotalBytes => GetHistoryStats().TotalBytes;

    public static void UpdateStartupRegistry(bool enable)
    {
        try
        {
            string appPath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(appPath))
            {
                appPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "InternetLimiter.exe");
                if (!System.IO.File.Exists(appPath))
                    appPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Valve.exe");
            }

            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key != null)
                {
                    key.DeleteValue("Valve", false);
                    if (enable && System.IO.File.Exists(appPath))
                    {
                        key.SetValue("InternetLimiter", $"\"{appPath}\" --startup");
                    }
                    else
                    {
                        key.DeleteValue("InternetLimiter", false);
                    }
                }
            }

            // Also check/clean HKLM if running with admin rights
            try
            {
                using var hklmKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (hklmKey != null)
                {
                    hklmKey.DeleteValue("Valve", false);
                    if (enable && System.IO.File.Exists(appPath))
                    {
                        hklmKey.SetValue("InternetLimiter", $"\"{appPath}\" --startup");
                    }
                    else
                    {
                        hklmKey.DeleteValue("InternetLimiter", false);
                    }
                }
            }
            catch { }

            // Manage User Startup shortcut
            try
            {
                string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string lnkPath = System.IO.Path.Combine(startupFolder, "Internet Limiter.lnk");
                if (enable && System.IO.File.Exists(appPath))
                {
                    ServiceManager.CreateLnk(lnkPath, appPath, AppDomain.CurrentDomain.BaseDirectory, appPath, "Internet Limiter - Network Bandwidth Shaper & Firewall", "--startup");
                }
                else if (System.IO.File.Exists(lnkPath))
                {
                    System.IO.File.Delete(lnkPath);
                }
            }
            catch { }
        }
        catch
        {
        }
    }

    public string DataFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve");

    public void Start()
    {
        _collector.IntervalSeconds = _settings.SampleIntervalSeconds;
        Fmt.UseBits = _settings.UseBits;
        _geo.Enabled = _settings.ResolveLocations;
        _history.RetentionDays = _settings.HistoryRetentionDays;
        _geo.Start();

        // Optional: only present if the signed driver is installed and loaded.
        _driver.TryOpen();
        _collector.Start();
        _ = SyncAllAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _serviceMonitorTimer?.Dispose(); } catch { }
        try { _divertShaper.OnPacketHeld -= OnPacketHeldByShaper; } catch { }
        try { _dns.Resolved -= OnDnsResolved; } catch { }
        try { _dns.Dispose(); } catch { }
        try { _collector.Sampled -= OnSampled; } catch { }
        try { _geo.Resolved -= OnGeoResolved; } catch { }
        try { _collector.Dispose(); } catch { }
        try { _history.Flush(); } catch { }
        try { _store.Save(_settings); } catch { }
        try { _geo.Dispose(); } catch { }
        try { _driver.Dispose(); } catch { }
        try { _divertShaper.Dispose(); } catch { }
        try { _shaper.ClearAllAsync().GetAwaiter().GetResult(); } catch { }
        try { (_firewall as IDisposable)?.Dispose(); } catch { }
    }

    private void OnSampled(TrafficSample sample)
    {
        _ui.BeginInvoke(() => Apply(sample));
    }

    private void Apply(TrafficSample sample)
    {
        TotalIn = 0;
        TotalOut = 0;

        _divertShaper.UpdatePortMap(sample.Connections.Where(c => c.LocalPort > 0).Select(c => (c.LocalPort, c.Pid)));

        var perProcess = new Dictionary<int, (double In, double Out, long InTotal, long OutTotal, int Conns)>();
        var split = new Dictionary<int, (double InetIn, double InetOut, double LocalIn, double LocalOut)>();

        var pidsRecordedFromConnections = new HashSet<int>();
        var connBytesRecorded = new Dictionary<int, (long In, long Out)>();
        foreach (ConnReading conn in sample.Connections)
        {
            TotalIn += conn.InRate;
            TotalOut += conn.OutRate;

            if (perProcess.TryGetValue(conn.Pid, out var agg))
            {
                perProcess[conn.Pid] = (agg.In + conn.InRate, agg.Out + conn.OutRate, agg.InTotal + conn.InTotal, agg.OutTotal + conn.OutTotal, agg.Conns + 1);
            }
            else
            {
                perProcess[conn.Pid] = (conn.InRate, conn.OutRate, conn.InTotal, conn.OutTotal, 1);
            }

            if (!_byConn.TryGetValue(conn.Key, out ConnRow? connRow))
            {
                connRow = new ConnRow
                {
                    Key = conn.Key,
                    Pid = conn.Pid,
                    Process = _collector.DisplayName(conn.Pid) is { Length: > 0 } p ? System.IO.Path.GetFileName(p) : $"pid {conn.Pid}",
                    Proto = conn.Proto,
                    Local = conn.Local,
                    Remote = conn.Remote,
                    RemoteIp = conn.RemoteIp,
                    Location = _geo.Lookup(conn.RemoteIp).Display,
                };
                _byConn[conn.Key] = connRow;
                Connections.Add(connRow);
                AddConnectionLog(connRow.Process, "Connect", conn.Proto, conn.Local, conn.Remote, connRow.Location, "Allow");
                string? newPath = _collector.DisplayName(conn.Pid);
                MaybeEnqueuePendingRequest(connRow, newPath);
            }

            _geo.Enqueue(conn.RemoteIp);
            _dns.Enqueue(conn.RemoteIp);

            connRow.InRate = conn.InRate;
            connRow.OutRate = conn.OutRate;
            connRow.IsInbound = conn.IsInbound;
            connRow.IsForwarded = conn.IsForwarded;
            connRow.InTotal = conn.InTotal;
            connRow.OutTotal = conn.OutTotal;
            connRow.RttMs = conn.RttMs;
            connRow.RetransBytes = conn.RetransBytes;

            split.TryGetValue(conn.Pid, out var halves);
            bool isLocalConn = GeoIp.IsPrivate(conn.RemoteIp) || conn.RemoteIp == "127.0.0.1" || conn.RemoteIp == "::1";
            split[conn.Pid] = isLocalConn
                ? (halves.InetIn, halves.InetOut, halves.LocalIn + conn.InRate, halves.LocalOut + conn.OutRate)
                : (halves.InetIn + conn.InRate, halves.InetOut + conn.OutRate, halves.LocalIn, halves.LocalOut);

            string path = _collector.DisplayName(conn.Pid); if (!string.IsNullOrEmpty(path)) _divertShaper.SetProcessPath(conn.Pid, path);
            if (!string.IsNullOrEmpty(path) && !IsTrafficIgnored(path))
            {
                if (conn.InRate > 0 || conn.OutRate > 0)
                {
                    pidsRecordedFromConnections.Add(conn.Pid);
                    long rIn = (long)Math.Max(0, conn.InRate);
                    long rOut = (long)Math.Max(0, conn.OutRate);
                    string app = System.IO.Path.GetFileName(path).ToLowerInvariant();
                    _history.Record(app, rIn, rOut, isLocal: isLocalConn);
                    _remoteIpHistory.Record(app, conn.RemoteIp, rIn, rOut, 1);
                    connBytesRecorded.TryGetValue(conn.Pid, out var rec);
                    connBytesRecorded[conn.Pid] = (rec.In + rIn, rec.Out + rOut);
                }
            }
        }

        if (_divertShaper.IsAvailable)
        {
            foreach (int pid in _divertShaper.GetActivePids())
            {
                if (pid > 0 && !perProcess.ContainsKey(pid))
                {
                    perProcess[pid] = (0, 0, 0, 0, 0);
                }
            }
        }

        bool newProcesses = false;
        foreach ((int pid, var agg) in perProcess)
        {
            if (!_byPid.TryGetValue(pid, out ProcessRow? row))
            {
                string path = _collector.DisplayName(pid); if (!string.IsNullOrEmpty(path)) _divertShaper.SetProcessPath(pid, path);
                string name = string.IsNullOrEmpty(path) ? $"pid {pid}" : System.IO.Path.GetFileName(path);
                row = new ProcessRow(pid, name, path);
                row.IsHidden = _settings.HiddenApps.Contains(row.Name);
                ApplyKnownRule(row);
                _byPid[pid] = row;
                Processes.Add(row);
                newProcesses = true;
            }

            // Always populate ESTATS measurements first (ensures non-zero rates even if engine has no PID attribution)
            row.InRate = agg.In;
            row.OutRate = agg.Out;
            row.InTotal = agg.InTotal;
            row.OutTotal = agg.OutTotal;
            row.Connections = agg.Conns;
        }

        if (newProcesses)
        {
            SyncDriver();
        }

        foreach (int pid in _byPid.Keys.Where(p => !perProcess.ContainsKey(p)).ToList())
        {
            var row = _byPid[pid];
            row.InRate = 0;
            row.OutRate = 0;

            int activeConns = sample.Connections.Count(c => c.Pid == pid);
            row.Connections = activeConns;
            if (activeConns > 0)
            {
                row.LastActiveUtc = DateTime.UtcNow;
            }

            bool isAlive = Valve.Interop.Native.IsProcessAlive(pid);
            row.IsAlive = isAlive;

            bool hasRulesOrLimits = row.DownloadLimitKbps > 0 || row.LimitKbps > 0 || row.IsBlocked;
            bool isRecent = (DateTime.UtcNow - row.LastActiveUtc).TotalSeconds < 120;

            if (!isAlive && activeConns == 0 && !isRecent && !hasRulesOrLimits)
            {
                Processes.Remove(row);
                _byPid.Remove(pid);
                _driverPrev.Remove(pid);
                if (_driver.IsAvailable)
                {
                    _driver.ClearLimit(pid);
                }
            }
        }
        foreach (string key in _byConn.Keys.Where(k => sample.Connections.All(c => c.Key != k)).ToList())
        {
            Connections.Remove(_byConn[key]);
            _byConn.Remove(key);
        }

        ApplyDriverRates(pidsRecordedFromConnections, connBytesRecorded, split);

        foreach (var c in Connections)
        {
            c.AddGraphSample(c.InRate, c.OutRate);
        }
        foreach (var p in Processes)
        {
            p.AddGraphSample(p.InRate, p.OutRate);
            if (!string.IsNullOrEmpty(p.Path))
            {
                TrackApplication(p.Path, p.Name, (long)p.InRate, (long)p.OutRate);
            }
        }

        // Recompute split and populate any processes not in ESTATS
        split = new Dictionary<int, (double InetIn, double InetOut, double LocalIn, double LocalOut)>();
        foreach (var conn in Connections)
        {
            split.TryGetValue(conn.Pid, out var halves);
            bool isLocalConn = GeoIp.IsPrivate(conn.RemoteIp) || conn.RemoteIp == "127.0.0.1" || conn.RemoteIp == "::1";
            split[conn.Pid] = isLocalConn
                ? (halves.InetIn, halves.InetOut, halves.LocalIn + conn.InRate, halves.LocalOut + conn.OutRate)
                : (halves.InetIn + conn.InRate, halves.InetOut + conn.OutRate, halves.LocalIn, halves.LocalOut);
        }

        foreach (var row in Processes)
        {
            if (row.InRate + row.OutRate <= 0) continue;

            if (!split.TryGetValue(row.Pid, out var s))
            {
                split[row.Pid] = (row.InRate, row.OutRate, 0, 0);
            }
            else
            {
                double connTotalIn = s.InetIn + s.LocalIn;
                double connTotalOut = s.InetOut + s.LocalOut;

                if (connTotalIn == 0 && connTotalOut == 0)
                {
                    split[row.Pid] = (row.InRate, row.OutRate, 0, 0);
                }
                else
                {
                    double excessIn = Math.Max(0, row.InRate - connTotalIn);
                    double excessOut = Math.Max(0, row.OutRate - connTotalOut);
                    split[row.Pid] = (s.InetIn + excessIn, s.InetOut + excessOut, s.LocalIn, s.LocalOut);
                }
            }
        }

        double peak = 1;
        foreach (ProcessRow row in Processes)
        {
            peak = Math.Max(peak, Math.Max(row.InRate, row.OutRate));
        }

        foreach (ProcessRow row in Processes)
        {
            row.InBar = row.InRate / peak;
            row.OutBar = row.OutRate / peak;
        }

        SortProcesses();
        UpdateFilterCounts();
        SyncTree(split);

        _graph.Add(new GraphSample(TotalIn, TotalOut));
        while (_graph.Count > 180)
        {
            _graph.RemoveAt(0);
        }

        GraphUpdated?.Invoke();
        if (SelectedConnection != null)
        {
            NodeGraphChanged?.Invoke(SelectedConnection.GraphHistory);
        }
        else if (SelectedProcess != null)
        {
            NodeGraphChanged?.Invoke(SelectedProcess.GraphHistory);
        }
        else
        {
            lock (_graph)
            {
                NodeGraphChanged?.Invoke(_graph.ToList());
            }
        }
        // Never suppress collector notes (UAC requirement / engine state)
        if (!Valve.Interop.Native.IsUserAnAdmin())
        {
            Note = "Rates need admin elevation (UAC)";
        }
        else if (_divertShaper.UnavailableReason == "driver files missing")
        {
            Note = "Packet engine unavailable: WinDivert driver files missing — monitoring only";
        }
        else if (!_divertShaper.IsAvailable && !_driver.IsAvailable)
        {
            Note = "Packet engine unavailable — monitoring only (ESTATS)";
        }
        else if (_divertShaper.IsAvailable && (DateTime.UtcNow - _divertShaper.LastPacketUtc).TotalSeconds > 5.0 && (TotalIn > 0 || TotalOut > 0))
        {
            Note = "Packet engine heartbeat stale — monitoring only (ESTATS)";
        }
        else
        {
            Note = sample.Note;
        }
        Status = $"{Processes.Count} processes · {Connections.Count} connections";

        foreach (ProcessRow proc in Processes)
        {
            if (!string.IsNullOrEmpty(proc.Path))
            {
                _divertShaper.SetProcessPath(proc.Pid, proc.Path);
            }
        }

        _tick++;
        if (_tick % 2 == 0)
        {
            RefreshNetworkAdapters();
        }

        if (_tick % 5 == 0)
        {
            RefreshQuotas();
            RefreshStats();
            foreach (ScheduleRow schedule in Schedules)
            {
                schedule.Refresh();
            }
            if (_settings.Rules != null && _settings.Rules.Count > 0)
            {
                RuleEvaluator.EvaluateScheduleActions(_settings.Rules, DateTime.Now);
                _divertShaper.SetUnifiedRules(_settings.Rules);
                // schedule state updated; WinDivertShaper already refreshed above
            }
        }

        if (_tick % 10 == 0)
        {
            _ = EnforceSchedulesAsync();
        }
    }

    private readonly Dictionary<string, TreeNode> _treeNodes = new();

    /// <summary>
    /// Reconciles the activity tree: machine at the root, the two zones and every
    /// process beneath it. Nodes are updated in place and only moved when the
    /// ranking changes, so selection and scroll position survive each tick.
    /// </summary>
    /// <summary>
    /// Rates straight from the kernel driver. Preferred over ESTATS because it is
    /// the same counter the throttling logic enforces against, and it covers both
    /// directions. Falls back silently when the driver is absent.
    /// </summary>
    private void ApplyDriverRates(ISet<int>? pidsRecordedFromConnections = null, Dictionary<int, (long In, long Out)>? connBytesRecorded = null, Dictionary<int, (double InetIn, double InetOut, double LocalIn, double LocalOut)>? split = null)
    {
        bool useService = IsConnectedToService;
        bool useDivert = _divertShaper.IsAvailable && (DateTime.UtcNow - _divertShaper.LastPacketUtc).TotalSeconds <= 5.0;
        bool useDriver = _driver.IsAvailable;

        DateTime now = DateTime.UtcNow;
        double dt = Math.Max(0.2, (now - _driverPrevAt).TotalSeconds);
        _driverPrevAt = now;

        if (!useService && !useDivert && !useDriver)
        {
            foreach (ProcessRow row in Processes)
            {
                if (row.InRate > 0 || row.OutRate > 0)
                {
                    long estIn = (long)(row.InRate * dt);
                    long estOut = (long)(row.OutRate * dt);
                    _sessionTotalInBytes += estIn;
                    _sessionTotalOutBytes += estOut;
                    bool isLocal = split != null && split.TryGetValue(row.Pid, out var procHalves) && (procHalves.LocalIn + procHalves.LocalOut > 0) && procHalves.InetIn == 0 && procHalves.InetOut == 0;
                    if (isLocal)
                    {
                        _sessionLocalInBytes += estIn;
                        _sessionLocalOutBytes += estOut;
                    }
                    else
                    {
                        _sessionInternetInBytes += estIn;
                        _sessionInternetOutBytes += estOut;
                    }
                }
            }
            return;
        }

        double sumIn = 0;
        double sumOut = 0;
        double sumDeltaIn = 0;
        double sumDeltaOut = 0;
        var compensatingRecorded = new HashSet<int>();

        foreach (ProcessRow row in Processes)
        {
            ulong bytesIn = 0, bytesOut = 0;
            bool gotStats = false;

            if (useService)
            {
                lock (_servicePerPidLock)
                {
                    if (_servicePerPidStats.TryGetValue(row.Pid, out var s))
                    {
                        bytesIn = s.In;
                        bytesOut = s.Out;
                        gotStats = true;
                    }
                }
            }
            else if (useDivert)
            {
                gotStats = _divertShaper.TryGetStats(row.Pid, out bytesIn, out bytesOut);
            }
            else if (useDriver)
            {
                gotStats = _driver.TryGetStats(row.Pid, out bytesIn, out bytesOut, out _, out _);
            }

            if (!gotStats)
            {
                // Engine has no fresh data for this PID - retain healthy ESTATS measurements!
                sumIn += row.InRate;
                sumOut += row.OutRate;
                continue;
            }

            bool isFirstSample = !_driverPrev.TryGetValue(row.Pid, out (ulong In, ulong Out) prev);
            _driverPrev[row.Pid] = (bytesIn, bytesOut);

            double deltaIn = (!isFirstSample && bytesIn >= prev.In) ? bytesIn - prev.In : 0;
            double deltaOut = (!isFirstSample && bytesOut >= prev.Out) ? bytesOut - prev.Out : 0;

            sumDeltaIn += deltaIn;
            sumDeltaOut += deltaOut;

            // Responsive rate integration: prioritize fresh driver/engine measurements
            if (deltaIn > 0 || deltaOut > 0)
            {
                double instantIn = deltaIn / dt;
                double instantOut = deltaOut / dt;

                if (deltaIn > 0)
                {
                    row.InRate = Math.Max(row.InRate, (0.75 * instantIn) + (0.25 * row.InRate));
                    if (row.InRate < 10) row.InRate = 0;
                }
                if (deltaOut > 0)
                {
                    row.OutRate = Math.Max(row.OutRate, (0.75 * instantOut) + (0.25 * row.OutRate));
                    if (row.OutRate < 10) row.OutRate = 0;
                }

                if (bytesIn > 0) row.InTotal = Math.Max(row.InTotal, (long)bytesIn);
                if (bytesOut > 0) row.OutTotal = Math.Max(row.OutTotal, (long)bytesOut);
            }
            else
            {
                // Engine observed 0 delta for this tick.
                // Do NOT wipe out healthy ESTATS measurements!
                // Only gently decay if the process has no active connections.
                if (row.Connections == 0)
                {
                    row.InRate = 0.5 * row.InRate;
                    row.OutRate = 0.5 * row.OutRate;
                    if (row.InRate < 250) row.InRate = 0;
                    if (row.OutRate < 250) row.OutRate = 0;
                }
            }

            sumIn += row.InRate;
            sumOut += row.OutRate;

            bool isLocalProcess = split != null && split.TryGetValue(row.Pid, out var halves) && (halves.LocalIn + halves.LocalOut > 0) && halves.InetIn == 0 && halves.InetOut == 0;

            if (deltaIn > 0 || deltaOut > 0)
            {
                if (isLocalProcess)
                {
                    _sessionLocalInBytes += (long)deltaIn;
                    _sessionLocalOutBytes += (long)deltaOut;
                }
                else
                {
                    _sessionInternetInBytes += (long)deltaIn;
                    _sessionInternetOutBytes += (long)deltaOut;
                }
                _sessionTotalInBytes += (long)deltaIn;
                _sessionTotalOutBytes += (long)deltaOut;
            }

            // Record to History / SQLite
            if (row.Pid > 0 && !string.IsNullOrEmpty(row.Path) && !IsTrafficIgnored(row.Path))
            {
                long recordedConnIn = 0;
                long recordedConnOut = 0;
                if (connBytesRecorded != null && connBytesRecorded.TryGetValue(row.Pid, out var recBytes))
                {
                    recordedConnIn = recBytes.In;
                    recordedConnOut = recBytes.Out;
                }

                long excessIn = Math.Max(0, (long)deltaIn - recordedConnIn);
                long excessOut = Math.Max(0, (long)deltaOut - recordedConnOut);

                if (excessIn > 0 || excessOut > 0)
                {
                    string app = System.IO.Path.GetFileName(row.Path).ToLowerInvariant();
                    _history.Record(app, excessIn, excessOut, isLocal: isLocalProcess);
                    compensatingRecorded.Add(row.Pid);
                }
                else if (pidsRecordedFromConnections != null && !pidsRecordedFromConnections.Contains(row.Pid))
                {
                    RecordCompensatingHistory(row.Pid, row.Path, row.InRate, row.OutRate, pidsRecordedFromConnections, compensatingRecorded);
                }
            }
        }

        if (useDivert && _divertShaper.TryGetTotalStats(out ulong totalInBytes, out ulong totalOutBytes))
        {
            if (_firstTotalSample)
            {
                _prevTotalIn = totalInBytes;
                _prevTotalOut = totalOutBytes;
                _firstTotalSample = false;
            }

            double totalDeltaIn = totalInBytes >= _prevTotalIn ? totalInBytes - _prevTotalIn : 0;
            double totalDeltaOut = totalOutBytes >= _prevTotalOut ? totalOutBytes - _prevTotalOut : 0;
            _prevTotalIn = totalInBytes;
            _prevTotalOut = totalOutBytes;

            double wireInRate = totalDeltaIn / dt;
            double wireOutRate = totalDeltaOut / dt;

            TotalIn = Math.Max(sumIn, wireInRate);
            TotalOut = Math.Max(sumOut, wireOutRate);

            if (totalDeltaIn > sumDeltaIn)
            {
                long unattrIn = (long)(totalDeltaIn - sumDeltaIn);
                _sessionTotalInBytes += unattrIn;
                _sessionInternetInBytes += unattrIn;
            }
            if (totalDeltaOut > sumDeltaOut)
            {
                long unattrOut = (long)(totalDeltaOut - sumDeltaOut);
                _sessionTotalOutBytes += unattrOut;
                _sessionInternetOutBytes += unattrOut;
            }
        }
        else
        {
            TotalIn = sumIn;
            TotalOut = sumOut;
        }

        var connsByPid = Connections.GroupBy(c => c.Pid).ToDictionary(g => g.Key, g => g.ToList());
        foreach (ProcessRow row in Processes)
        {
            if (connsByPid.TryGetValue(row.Pid, out var conns) && conns.Count > 0)
            {
                double sumInRate = conns.Sum(c => c.InRate);
                double sumOutRate = conns.Sum(c => c.OutRate);

                // Zero process rate must NEVER zero out healthy connection rates!
                // Scale if both process and connection rates are positive; distribute if connections were unmetered
                if (row.InRate > 0 && sumInRate > 0)
                {
                    double scaleIn = Math.Clamp(row.InRate / sumInRate, 0.1, 10.0);
                    foreach (var c in conns)
                    {
                        c.InRate *= scaleIn;
                    }
                }
                else if (row.InRate > 0 && sumInRate == 0)
                {
                    double perConn = row.InRate / conns.Count;
                    foreach (var c in conns)
                    {
                        c.InRate = perConn;
                    }
                }

                if (row.OutRate > 0 && sumOutRate > 0)
                {
                    double scaleOut = Math.Clamp(row.OutRate / sumOutRate, 0.1, 10.0);
                    foreach (var c in conns)
                    {
                        c.OutRate *= scaleOut;
                    }
                }
                else if (row.OutRate > 0 && sumOutRate == 0)
                {
                    double perConn = row.OutRate / conns.Count;
                    foreach (var c in conns)
                    {
                        c.OutRate = perConn;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Records history for active processes while strictly preventing double-counting (B2).
    /// Connections are the primary source; compensating process records are only written
    /// for processes with engine traffic that had zero connections this tick.
    /// </summary>
    public void RecordCompensatingHistory(int pid, string path, double inRate, double outRate, ISet<int> pidsRecordedFromConnections, ISet<int> compensatingRecordedPids)
    {
        if (pid <= 0 || string.IsNullOrEmpty(path)) return;
        if (pidsRecordedFromConnections.Contains(pid)) return; // Already recorded via connection rows
        if (!compensatingRecordedPids.Add(pid)) return; // Already recorded once this tick
        if (inRate <= 0 && outRate <= 0) return;
        if (IsTrafficIgnored(path)) return;

        string app = System.IO.Path.GetFileName(path).ToLowerInvariant();
        _history.Record(app, (long)Math.Max(0, inRate), (long)Math.Max(0, outRate));
    }
    public string SortColumn { get; set; } = "";
    public bool SortAscending { get; set; } = false;

    private Dictionary<int, (double InetIn, double InetOut, double LocalIn, double LocalOut)> _lastSplit = new();

    public void RefreshTree()
    {
        _ui.BeginInvoke(() => SyncTree(_lastSplit));
    }

    private void SyncTree(Dictionary<int, (double InetIn, double InetOut, double LocalIn, double LocalOut)> split)
    {
        _lastSplit = split;
        double inetIn = 0, inetOut = 0, localIn = 0, localOut = 0;
        foreach ((double iIn, double iOut, double lIn, double lOut) in split.Values)
        {
            inetIn += iIn;
            inetOut += iOut;
            localIn += lIn;
            localOut += lOut;
        }

        TreeNode machine = Node("machine", 0, Environment.MachineName, "", null, null);
        TreeNode local = Node("zone:local", 1, "LocalNetwork", "", null, null);
        TreeNode internet = Node("zone:internet", 1, "Internet", "", null, null);
        TreeNode filtersRoot = Node("node:filters", 1, "Filters", "", null, null);
        machine.CanExpand = true;
        local.CanExpand = false;
        internet.CanExpand = false;
        filtersRoot.CanExpand = FilterRules.Count > 0;

        var desired = new List<TreeNode> { machine };
        if (machine.IsExpanded)
        {
            desired.Add(local);
            desired.Add(internet);
            desired.Add(filtersRoot);

            if (filtersRoot.IsExpanded && FilterRules.Count > 0)
            {
                double totalFiltersIn = 0;
                double totalFiltersOut = 0;
                foreach (var filter in FilterRules)
                {
                    TreeNode filterNode = Node($"filter:{filter.Id}", 2, filter.Name, "", null, null);
                    double dlRate = 0, ulRate = 0;
                    int matchCount = 0;
                    var matchingConns = new List<ConnRow>();
                    foreach (var conn in Connections)
                    {
                        var proc = _byPid.TryGetValue(conn.Pid, out var p) ? p : null;
                        var ctx = new ConnectionContext { AppName = proc?.Name ?? "", AppPath = proc?.Path ?? "", LocalIp = conn.LocalIp, LocalPort = (ushort)conn.LocalPort, RemoteIp = conn.RemoteIp, RemotePort = (ushort)conn.RemotePort, Protocol = conn.Proto, Domain = conn.Hostname, IsInbound = conn.IsInbound };
                        if (filter.Matches(ctx))
                        {
                            dlRate += conn.InRate;
                            ulRate += conn.OutRate;
                            matchCount++;
                            matchingConns.Add(conn);
                        }
                    }
                    filterNode.InRate = dlRate;
                    filterNode.OutRate = ulRate;
                    filterNode.Children = matchCount;
                    filterNode.CanExpand = matchCount > 0;
                    desired.Add(filterNode);
                    totalFiltersIn += dlRate;
                    totalFiltersOut += ulRate;

                    if (filterNode.IsExpanded && matchCount > 0)
                    {
                        foreach (var conn in matchingConns)
                        {
                            var proc = _byPid.TryGetValue(conn.Pid, out var p) ? p : null;
                            desired.Add(Node($"filterconn:{filter.Id}:{conn.Key}", 3, conn.Remote, conn.Location, proc, conn));
                        }
                    }
                }
                filtersRoot.InRate = totalFiltersIn;
                filtersRoot.OutRate = totalFiltersOut;
            }

            var connsByPid = Connections.ToLookup(c => c.Pid);
            var appGroups = Processes
                .GroupBy(p => p.Name)
                .ToList();

            IEnumerable<IGrouping<string, ProcessRow>> sortedGroups = SortColumn switch
            {
                "Name" => SortAscending
                    ? appGroups.OrderBy(g => g.Key)
                    : appGroups.OrderByDescending(g => g.Key),
                "DL Rate" => SortAscending
                    ? appGroups.OrderBy(g => g.Sum(p => p.InRate))
                    : appGroups.OrderByDescending(g => g.Sum(p => p.InRate)),
                "UL Rate" => SortAscending
                    ? appGroups.OrderBy(g => g.Sum(p => p.OutRate))
                    : appGroups.OrderByDescending(g => g.Sum(p => p.OutRate)),
                "DL Total" => SortAscending
                    ? appGroups.OrderBy(g => g.Sum(p => p.InTotal))
                    : appGroups.OrderByDescending(g => g.Sum(p => p.InTotal)),
                "UL Total" => SortAscending
                    ? appGroups.OrderBy(g => g.Sum(p => p.OutTotal))
                    : appGroups.OrderByDescending(g => g.Sum(p => p.OutTotal)),
                "DL Limit" => SortAscending
                    ? appGroups.OrderBy(g => g.Max(p => p.DownloadLimitKbps))
                    : appGroups.OrderByDescending(g => g.Max(p => p.DownloadLimitKbps)),
                "UL Limit" => SortAscending
                    ? appGroups.OrderBy(g => g.Max(p => p.LimitKbps))
                    : appGroups.OrderByDescending(g => g.Max(p => p.LimitKbps)),
                "Status" => SortAscending
                    ? appGroups.OrderBy(g => g.Any(p => p.InRate + p.OutRate > 0))
                    : appGroups.OrderByDescending(g => g.Any(p => p.InRate + p.OutRate > 0)),
                _ => SortAscending
                    ? appGroups.OrderBy(g => g.Sum(p => p.InRate + p.OutRate))
                    : appGroups.OrderByDescending(g => g.Sum(p => p.InRate + p.OutRate)),
            };

            foreach (var group in sortedGroups)
            {
                var procs = SortColumn switch
                {
                    "Name" => SortAscending ? group.OrderBy(p => p.Name).ToList() : group.OrderByDescending(p => p.Name).ToList(),
                    "DL Rate" => SortAscending ? group.OrderBy(p => p.InRate).ToList() : group.OrderByDescending(p => p.InRate).ToList(),
                    "UL Rate" => SortAscending ? group.OrderBy(p => p.OutRate).ToList() : group.OrderByDescending(p => p.OutRate).ToList(),
                    "DL Total" => SortAscending ? group.OrderBy(p => p.InTotal).ToList() : group.OrderByDescending(p => p.InTotal).ToList(),
                    "UL Total" => SortAscending ? group.OrderBy(p => p.OutTotal).ToList() : group.OrderByDescending(p => p.OutTotal).ToList(),
                    _ => SortAscending ? group.OrderBy(p => p.InRate + p.OutRate).ToList() : group.OrderByDescending(p => p.InRate + p.OutRate).ToList(),
                };

                if (procs.Count == 1)
                {
                    var process = procs[0];
                    var conns = SortColumn switch
                    {
                        "DL Rate" => SortAscending ? connsByPid[process.Pid].OrderBy(c => c.InRate).ToList() : connsByPid[process.Pid].OrderByDescending(c => c.InRate).ToList(),
                        "UL Rate" => SortAscending ? connsByPid[process.Pid].OrderBy(c => c.OutRate).ToList() : connsByPid[process.Pid].OrderByDescending(c => c.OutRate).ToList(),
                        "Name" => SortAscending ? connsByPid[process.Pid].OrderBy(c => c.Remote).ToList() : connsByPid[process.Pid].OrderByDescending(c => c.Remote).ToList(),
                        _ => SortAscending ? connsByPid[process.Pid].OrderBy(c => c.InRate + c.OutRate).ToList() : connsByPid[process.Pid].OrderByDescending(c => c.InRate + c.OutRate).ToList(),
                    };

                    TreeNode procNode = Node($"app:{process.Pid}", 1, process.Name, process.Path, process, null);
                    procNode.Children = conns.Count;
                    procNode.CanExpand = conns.Count > 0;
                    desired.Add(procNode);

                    if (procNode.IsExpanded && conns.Count > 0)
                    {
                        foreach (ConnRow conn in conns)
                        {
                            TreeNode connNode = Node($"conn:{process.Pid}:{conn.Key}", 2, conn.Remote, conn.Location, process, conn);
                            desired.Add(connNode);
                        }
                    }
                }
                else
                {
                    var first = procs[0];
                    int totalGroupConns = procs.Sum(p => connsByPid[p.Pid].Count());
                    TreeNode groupNode = Node($"group:{group.Key}", 1, group.Key, first.Path, first, null);
                    groupNode.Children = procs.Count;
                    groupNode.CanExpand = true;
                    desired.Add(groupNode);

                    if (groupNode.IsExpanded)
                    {
                        foreach (var process in procs)
                        {
                            var conns = SortColumn switch
                            {
                                "DL Rate" => SortAscending ? connsByPid[process.Pid].OrderBy(c => c.InRate).ToList() : connsByPid[process.Pid].OrderByDescending(c => c.InRate).ToList(),
                                "UL Rate" => SortAscending ? connsByPid[process.Pid].OrderBy(c => c.OutRate).ToList() : connsByPid[process.Pid].OrderByDescending(c => c.OutRate).ToList(),
                                "Name" => SortAscending ? connsByPid[process.Pid].OrderBy(c => c.Remote).ToList() : connsByPid[process.Pid].OrderByDescending(c => c.Remote).ToList(),
                                _ => SortAscending ? connsByPid[process.Pid].OrderBy(c => c.InRate + c.OutRate).ToList() : connsByPid[process.Pid].OrderByDescending(c => c.InRate + c.OutRate).ToList(),
                            };

                            TreeNode procNode = Node($"app:{process.Pid}", 2, $"Process {process.Pid}", process.Path, process, null);
                            procNode.Children = conns.Count;
                            procNode.CanExpand = conns.Count > 0;
                            desired.Add(procNode);

                            if (procNode.IsExpanded && conns.Count > 0)
                            {
                                foreach (ConnRow conn in conns)
                                {
                                    TreeNode connNode = Node($"conn:{process.Pid}:{conn.Key}", 3, conn.Remote, conn.Location, process, conn);
                                    desired.Add(connNode);
                                }
                            }
                        }
                    }
                }
            }
        }

        foreach (TreeNode stale in TreeRows.Where(n => !desired.Contains(n)).ToList())
        {
            TreeRows.Remove(stale);
            _treeNodes.Remove(stale.Key);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            int current = TreeRows.IndexOf(desired[i]);
            if (current < 0)
            {
                TreeRows.Insert(Math.Min(i, TreeRows.Count), desired[i]);
            }
            else if (current != i)
            {
                TreeRows.Move(current, i);
            }
        }

        double peak = 1;
        foreach (ProcessRow process in Processes)
        {
            peak = Math.Max(peak, Math.Max(process.InRate, process.OutRate));
        }

        machine.InRate = TotalIn;
        machine.OutRate = TotalOut;
        machine.GroupInTotal = _sessionTotalInBytes;
        machine.GroupOutTotal = _sessionTotalOutBytes;
        machine.InFraction = Math.Min(1, machine.InRate / peak);
        machine.OutFraction = Math.Min(1, machine.OutRate / peak);
        machine.Children = Processes.Count + 2;
        machine.IsOnline = true;
        machine.RefreshDerived();

        local.InRate = localIn;
        local.OutRate = localOut;
        local.GroupInTotal = _sessionLocalInBytes;
        local.GroupOutTotal = _sessionLocalOutBytes;
        local.InFraction = Math.Min(1, localIn / peak);
        local.OutFraction = Math.Min(1, localOut / peak);
        local.Children = Processes.Count(p => p.Connections > 0);
        local.IsOnline = true;
        local.RefreshDerived();

        internet.InRate = Math.Max(0, TotalIn - localIn);
        internet.OutRate = Math.Max(0, TotalOut - localOut);
        internet.GroupInTotal = Math.Max(0, _sessionTotalInBytes - _sessionLocalInBytes);
        internet.GroupOutTotal = Math.Max(0, _sessionTotalOutBytes - _sessionLocalOutBytes);
        internet.InFraction = Math.Min(1, internet.InRate / peak);
        internet.OutFraction = Math.Min(1, internet.OutRate / peak);
        internet.Children = Processes.Count;
        internet.IsOnline = true;
        internet.RefreshDerived();

        filtersRoot.RefreshDerived();

        foreach (TreeNode node in desired)
        {
            if (node.Connection is not null)
            {
                node.InRate = node.Connection.InRate;
                node.OutRate = node.Connection.OutRate;
                node.InFraction = Math.Min(1, node.InRate / peak);
                node.OutFraction = Math.Min(1, node.OutRate / peak);
                node.IsOnline = true;
                node.RefreshDerived();
            }
            else if (node.Key.StartsWith("group:"))
            {
                string grpName = node.Key.Substring(6);
                var procs = Processes.Where(p => p.Name == grpName).ToList();
                double gIn = procs.Sum(p => p.InRate);
                double gOut = procs.Sum(p => p.OutRate);
                node.InRate = gIn;
                node.OutRate = gOut;
                node.GroupInTotal = procs.Sum(p => p.InTotal);
                node.GroupOutTotal = procs.Sum(p => p.OutTotal);
                node.InFraction = Math.Min(1, gIn / peak);
                node.OutFraction = Math.Min(1, gOut / peak);
                node.IsOnline = procs.Any(p => p.IsOnline);
                node.RefreshDerived();
            }
            else if (node.Process is not null)
            {
                node.InRate = node.Process.InRate;
                node.OutRate = node.Process.OutRate;
                node.InFraction = Math.Min(1, node.InRate / peak);
                node.OutFraction = Math.Min(1, node.OutRate / peak);
                node.IsOnline = node.Process.IsOnline;
                node.RefreshDerived();
            }
        }
    }

    private TreeNode Node(string key, int level, string name, string path, ProcessRow? process, ConnRow? connection)
    {
        if (_treeNodes.TryGetValue(key, out TreeNode? existing))
        {
            return existing;
        }

        var node = new TreeNode(level, key, name, path, process, connection);
        node.IsExpanded = (level == 0);
        _treeNodes[key] = node;
        return node;
    }

    private void UpdateFilterCounts()
    {
        if (Filters.Count < 5)
        {
            return;
        }

        Filters[0].Count = Processes.Count;
        Filters[1].Count = Processes.Count(p => p.InRate > 1);
        Filters[2].Count = Processes.Count(p => p.OutRate > 1);
        Filters[3].Count = Processes.Count(p => p.HasLimit);
        Filters[4].Count = Processes.Count(p => p.IsBlocked);
        Raise(nameof(AllCountText));
        Raise(nameof(OnlineCountText));
        Raise(nameof(OfflineCountText));
        Raise(nameof(BlockedCountText));
    }

    private void SortProcesses()
    {
        // Reordering the bound collection every second fights the DataGrid's selection,
        // so only reorder when the ranking has actually changed.
        List<ProcessRow> sorted = Processes.OrderByDescending(p => p.InRate + p.OutRate).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int current = Processes.IndexOf(sorted[i]);
            if (current != i)
            {
                Processes.Move(current, i);
            }
        }
    }

    private void ApplyKnownRule(ProcessRow row)
    {
        row.DownloadLimitKbps = 0;
        row.DownloadLimitEnabled = false;
        row.LimitKbps = 0;
        row.UploadLimitKbps = 0;
        row.UploadLimitEnabled = false;
        AppRule? rule = FindRule(row.Name);
        if (rule is null)
        {
            return;
        }

        row.LimitKbps = rule.UploadLimitKbps;
        row.UploadLimitKbps = rule.UploadLimitKbps;
        row.UploadLimitEnabled = rule.UploadLimitEnabled && rule.UploadLimitKbps > 0;
        row.DownloadLimitKbps = rule.DownloadLimitKbps;
        row.DownloadLimitEnabled = rule.DownloadLimitEnabled && rule.DownloadLimitKbps > 0;
        row.IsBlocked = rule.Blocked;
        row.Priority = rule.Priority;
    }

    private AppRule? FindRule(string app)
        => _settings.Apps.FirstOrDefault(a => string.Equals(a.App, app, StringComparison.OrdinalIgnoreCase));

    private AppRule EnsureRule(string app, string path)
    {
        AppRule? rule = FindRule(app);
        if (rule is null)
        {
            rule = new AppRule { App = app.ToLowerInvariant(), AppPath = path };
            _settings.Apps.Add(rule);
            rule.DownloadLimitEnabled = false;
            rule.UploadLimitEnabled = false;
            Rules.Add(new RuleRow(rule));
        }
        else if (string.IsNullOrEmpty(rule.AppPath) && !string.IsNullOrEmpty(path))
        {
            rule.AppPath = path;
        }

        return rule;
    }

    public async Task SetCapAsync(ProcessRow row, int kbps)
    {
        if (!CheckControlPermission()) return;
        AppRule rule = EnsureRule(row.Name, row.Path);
        rule.UploadLimitKbps = kbps;
        rule.UploadLimitEnabled = kbps > 0;
        row.LimitKbps = kbps;
        row.UploadLimitKbps = kbps;
        row.UploadLimitEnabled = kbps > 0;
        _divertShaper.SetAppLimit(rule.App, kbps, isDownload: false, enabled: kbps > 0);
        if (!string.IsNullOrEmpty(rule.AppPath) && !string.Equals(rule.AppPath, rule.App, StringComparison.OrdinalIgnoreCase))
        {
            _divertShaper.SetAppLimit(rule.AppPath, kbps, isDownload: false, enabled: kbps > 0);
        }
        foreach (ProcessRow live in Processes)
        {
            if (string.Equals(live.Name, row.Name, StringComparison.OrdinalIgnoreCase))
            {
                live.LimitKbps = kbps;
                live.UploadLimitKbps = kbps;
                live.UploadLimitEnabled = kbps > 0;
                if (live.Pid > 0)
                {
                    _divertShaper.SetProcessLimit(live.Pid, kbps, isDownload: false, enabled: kbps > 0);
                }
            }
        }
        NotifyServiceAppLimit(rule.App, kbps, isDownload: false, enabled: kbps > 0);
        row.RefreshText();
        SyncDriver();

        if (kbps <= 0)
        {
            // ClearAsync drops the priority policy too, so restore it afterwards.
            ShaperResult result = await _shaper.ClearAsync(rule.AppPath);
            if (rule.Priority != TrafficPriority.Normal)
            {
                await _shaper.SetPriorityAsync(rule.AppPath, rule.Priority);
            }

            Status = result.Message;
        }
        else
        {
            ShaperResult result = await _shaper.SetUploadLimitAsync(rule.AppPath, kbps);
            Status = result.Message;
        }

        Persist();
    }

    public void ToggleDownloadLimit(ProcessRow row)
    {
        if (!CheckControlPermission()) return;
        AppRule rule = EnsureRule(row.Name, row.Path);
        bool newState = !rule.DownloadLimitEnabled;
        rule.DownloadLimitEnabled = newState;
        row.DownloadLimitEnabled = newState;
        _divertShaper.SetAppLimit(rule.App, rule.EffectiveDownloadLimitKbps, isDownload: true, enabled: newState);
        if (row.Pid > 0)
        {
            _divertShaper.SetProcessLimit(row.Pid, rule.EffectiveDownloadLimitKbps, isDownload: true, enabled: newState);
        }
        row.RefreshText();
        Persist();
        Status = $"download limit for {row.Name} {(newState ? "enabled" : "disabled")}";
    }

    public void ToggleUploadLimit(ProcessRow row)
    {
        if (!CheckControlPermission()) return;
        AppRule rule = EnsureRule(row.Name, row.Path);
        bool newState = !rule.UploadLimitEnabled;
        rule.UploadLimitEnabled = newState;
        row.UploadLimitEnabled = newState;
        _divertShaper.SetAppLimit(rule.App, rule.EffectiveUploadLimitKbps, isDownload: false, enabled: newState);
        if (row.Pid > 0)
        {
            _divertShaper.SetProcessLimit(row.Pid, rule.EffectiveUploadLimitKbps, isDownload: false, enabled: newState);
        }
        row.RefreshText();
        Persist();
        Status = $"upload limit for {row.Name} {(newState ? "enabled" : "disabled")}";
    }

    public async Task SetBothLimitsAsync(ProcessRow row, int kbps)
    {
        if (!CheckControlPermission()) return;
        AppRule rule = EnsureRule(row.Name, row.Path);
        rule.BothDirectionsCoupled = true;
        await SetCapAsync(row, kbps);
        await SetDownloadCapAsync(row, kbps);
    }

    public async Task SetPriorityAsync(ProcessRow row, TrafficPriority priority)
    {
        if (!CheckControlPermission()) return;
        AppRule rule = EnsureRule(row.Name, row.Path);
        rule.Priority = priority;
        row.Priority = priority;
        _divertShaper.SetAppPriority(rule.App, priority);
        if (row.Pid > 0)
        {
            _divertShaper.SetProcessPriority(row.Pid, priority);
        }

        ShaperResult result = await _shaper.SetPriorityAsync(rule.AppPath, priority);
        Status = result.Message;
        Persist();
    }

    public void SetConnectionDownloadLimit(ConnRow conn, int kbps)
    {
        string key = $"{conn.Local}:{conn.Remote}:{conn.Proto}";
        _settings.ConnectionDownloadLimits[key] = kbps;
        conn.DownloadLimitKbps = kbps;
        conn.DownloadLimitEnabled = kbps > 0;
        _divertShaper.SetConnectionLimit(key, kbps, isDownload: true, enabled: kbps > 0);
        conn.Refresh();
        Persist();
        Status = kbps > 0 ? $"connection DL limit set: {kbps} kbit/s" : "connection DL limit removed";
    }

    public void SetConnectionUploadLimit(ConnRow conn, int kbps)
    {
        string key = $"{conn.Local}:{conn.Remote}:{conn.Proto}";
        _settings.ConnectionLimits[key] = kbps;
        conn.UploadLimitKbps = kbps;
        conn.UploadLimitEnabled = kbps > 0;
        _divertShaper.SetConnectionLimit(key, kbps, isDownload: false, enabled: kbps > 0);
        conn.Refresh();
        Persist();
        Status = kbps > 0 ? $"connection UL limit set: {kbps} kbit/s" : "connection UL limit removed";
    }

    public void SetConnectionPriority(ConnRow conn, TrafficPriority priority)
    {
        if (!CheckControlPermission()) return;
        string key = $"{conn.Local}:{conn.Remote}:{conn.Proto}";
        _settings.ConnectionPriorities[key] = priority;
        conn.Priority = priority;
        _divertShaper.SetConnectionPriority(key, priority);
        conn.Refresh();
        Persist();
        Status = $"connection priority set to {priority}";
    }

    public void KillConnection(ConnRow conn)
    {
        if (!CheckControlPermission()) return;
        if (conn == null) return;
        _divertShaper.KillConnection(conn.LocalIp, conn.LocalPort, conn.RemoteIp, conn.RemotePort);
        Collector.KillTcpConnection(conn.LocalIp, conn.LocalPort, conn.RemoteIp, conn.RemotePort);
        Status = $"Killed connection {conn.Local} -> {conn.Remote}";
        AddConnectionLog(conn.ProcessName, "Killed", conn.Proto, conn.Local, conn.Remote, "-", "Terminated");
    }

    public void KillAppConnections(ProcessRow proc)
    {
        if (!CheckControlPermission()) return;
        if (proc == null) return;
        if (proc.Pid > 0)
        {
            Collector.KillConnections(proc.Pid);
        }
        var appConns = Connections.Where(c => c.Pid == proc.Pid || string.Equals(c.ProcessName, proc.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var c in appConns)
        {
            _divertShaper.KillConnection(c.LocalIp, c.LocalPort, c.RemoteIp, c.RemotePort);
        }
        Status = $"Killed all {appConns.Count} connections for {proc.Name} (PID {proc.Pid})";
        AddConnectionLog(proc.Name, "KilledAllConnections", "All", "-", "-", "-", "Terminated");
    }

    public void TerminateProcess(ProcessRow proc)
    {
        if (proc == null || proc.Pid <= 0) return;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(proc.Pid);
            p.Kill();
            Collector.KillConnections(proc.Pid);
            Status = $"Terminated process {proc.Name} (PID {proc.Pid})";
            AddConnectionLog(proc.Name, "TerminatedProcess", "All", "-", "-", "-", "Killed");
        }
        catch (Exception ex)
        {
            Status = $"Failed to terminate process {proc.Pid}: {ex.Message}";
        }
    }

    public async Task SetBlockedAsync(ProcessRow row, bool blocked)
    {
        AppRule rule = EnsureRule(row.Name, row.Path);
        rule.Blocked = blocked;
        rule.BlockerIn = blocked ? BlockerState.Deny : BlockerState.NotSet;
        rule.BlockerOut = blocked ? BlockerState.Deny : BlockerState.NotSet;
        row.IsBlocked = blocked;

        await SyncFirewallAsync();
        if (blocked && row.Pid > 0)
        {
            Collector.KillConnections(row.Pid);
        }
        AddConnectionLog(row.Name, blocked ? "Blocked" : "Unblocked", "All", "-", "-", "-", blocked ? "Deny" : "Allow");
        Persist();
    }

    /// <summary>3.04 "Remember" action: persist an allow/deny decision for an app so it never asks again.</summary>
    public async Task RememberBlockerAsync(ProcessRow row, bool deny)
    {
        if (!CheckControlPermission()) return;
        AppRule rule = EnsureRule(row.Name, row.Path);
        if (deny)
        {
            rule.Blocked = true;
            rule.BlockerIn = BlockerState.Deny;
            rule.BlockerOut = BlockerState.Deny;
            row.IsBlocked = true;
            if (row.Pid > 0) Collector.KillConnections(row.Pid);
        }
        else
        {
            rule.Blocked = false;
            rule.BlockerIn = BlockerState.Allow;
            rule.BlockerOut = BlockerState.Allow;
            row.IsBlocked = false;
        }

        await SyncFirewallAsync();
        AddConnectionLog(row.Name, deny ? "Blocked" : "Allowed", "All", "-", "-", "-", deny ? "Deny" : "Allow");
        Persist();
        Report($"{row.Name}: {(deny ? "blocked permanently" : "allowed permanently")}");
    }

    // ============================================================================
    // 3.04 REAL Ask queue: new connections under an Ask policy wait here until the
    // user answers. While pending, the process is temporarily denied so the
    // question is honest - the traffic really is held back until a decision.
    // ============================================================================

    private static string PendingKey(string app, string remoteIp) => $"{app}|{remoteIp}";

    private void MaybeEnqueuePendingRequest(ConnRow row, string? path)
    {
        if (!_settings.BlockerEnabled)
        {
            return;
        }

        if (string.IsNullOrEmpty(row.RemoteIp) || row.RemoteIp == "*")
        {
            return;
        }

        // Essential loopback and system traffic is never intercepted for blocker prompt
        if (row.RemoteIp.StartsWith("127.") || row.RemoteIp == "::1" || row.Pid <= 4)
        {
            return;
        }

        string app = string.IsNullOrEmpty(path)
            ? row.Process.ToLowerInvariant()
            : System.IO.Path.GetFileName(path).ToLowerInvariant();

        bool asks = AskOnNewEndpoint;
        if (!asks)
        {
            AppRule? rule = _settings.Apps.FirstOrDefault(a => string.Equals(a.App, app, StringComparison.OrdinalIgnoreCase));
            asks = rule is { BlockerOut: BlockerState.Ask } or { BlockerIn: BlockerState.Ask };
            if (!asks)
            {
                asks = string.Equals(_settings.DefaultBlockerAction, "Ask", StringComparison.OrdinalIgnoreCase)
                    && rule is null;
            }
        }

        if (!asks)
        {
            return;
        }

        string key = PendingKey(app, row.RemoteIp);
        if (!_pendingSeen.Add(key))
        {
            return; // already waiting on a decision for this app+remote
        }

        var item = new PendingRequestItem
        {
            HeaderText = $"{(row.IsInbound ? "Inbound" : "Outgoing")} connection - {row.Proto}",
            AppName = row.Process,
            Path = path ?? "",
            Pid = row.Pid,
            LocalEndpoint = row.Local,
            RemoteEndpoint = row.Remote,
            Icon = Icons.ForPath(path),
        };
        PendingRequests.Add(item);
        ToastWindow.ShowToast("Blocker: Connection Request", $"{item.AppName} wants to connect to {item.RemoteEndpoint}", primaryLabel: "Review", isWarning: false);

        Report($"{app}: new connection to {row.RemoteIp} is waiting for your decision (Ask)");
    }

    /// <summary>Removes an item from the pending queue without changing rules (used by temporary actions).</summary>
    public void DismissPendingRequest(PendingRequestItem item)
    {
        PendingRequests.Remove(item);
        string ip = item.RemoteEndpoint.Contains(':') ? item.RemoteEndpoint.Split(':')[0] : item.RemoteEndpoint;
        _pendingSeen.Remove(PendingKey(item.AppName.ToLowerInvariant(), ip));

        if (!string.IsNullOrEmpty(item.FlowKey))
        {
            _divertShaper.ReleaseHeldFlow(item.FlowKey, allow: false, remember: false);
        }
        if (item.PacketId > 0)
        {
            _divertShaper.ReleaseHeldPacket(item.PacketId, allow: false, rememberFlow: false);
            if (IsConnectedToService)
            {
                try
                {
                    var payload = System.Text.Json.JsonSerializer.Serialize(new WinDivertShaper.ResolvePacketPayload
                    {
                        packetId = item.PacketId,
                        allow = false,
                        rememberFlow = false,
                        durationSeconds = 0
                    });
                    ValveIpcClient.SendCommand("RESOLVE_PACKET", payload);
                }
                catch { }
            }
        }
    }

    /// <summary>Resolves one pending Ask request. remember=true persists the decision as a permanent rule.</summary>
    public async Task ResolvePendingRequest(PendingRequestItem item, bool allow, bool remember)
    {
        DismissPendingRequest(item);

        if (!string.IsNullOrEmpty(item.FlowKey))
        {
            _divertShaper.ReleaseHeldFlow(item.FlowKey, allow, remember);
        }
        if (item.PacketId > 0)
        {
            _divertShaper.ReleaseHeldPacket(item.PacketId, allow, remember);
            if (IsConnectedToService)
            {
                try
                {
                    var payload = System.Text.Json.JsonSerializer.Serialize(new WinDivertShaper.ResolvePacketPayload
                    {
                        packetId = item.PacketId,
                        allow = allow,
                        rememberFlow = remember,
                        durationSeconds = 0
                    });
                    ValveIpcClient.SendCommand("RESOLVE_PACKET", payload);
                }
                catch { }
            }
        }
        _divertShaper.ReleaseHeldApp(item.AppName, allow, remember);

        if (_byPid.TryGetValue(item.Pid, out ProcessRow? process))
        {
            if (remember)
            {
                await RememberBlockerAsync(process, deny: !allow);
            }
            else
            {
                await SetBlockedAsync(process, blocked: !allow);
            }
        }
        else if (_settings.Apps.FirstOrDefault(a => string.Equals(a.App, item.AppName, StringComparison.OrdinalIgnoreCase)) is { } orphan)
        {
            // Process is gone but a rule was created while it was pending - keep the rule honest.
            if (remember)
            {
                orphan.Blocked = !allow;
                orphan.BlockerIn = allow ? BlockerState.Allow : BlockerState.Deny;
                orphan.BlockerOut = allow ? BlockerState.Allow : BlockerState.Deny;
                Persist();
                await SyncFirewallAsync();
            }
        }

        Report($"{item.AppName}: {(allow ? "allowed" : "denied")} connection to {item.RemoteEndpoint}{(remember ? " (remembered)" : "")}");
    }

    private void OnPacketHeldByShaper(WinDivertShaper.HeldPacket held)
    {
        _ui.BeginInvoke(() =>
        {
            string app = string.IsNullOrEmpty(held.ProcessName) ? $"pid_{held.Pid}" : held.ProcessName.ToLowerInvariant();
            string key = PendingKey(app, held.RemoteIp);
            if (!_pendingSeen.Add(key))
            {
                return;
            }

            var item = new PendingRequestItem
            {
                PacketId = held.Id,
                FlowKey = held.FlowKey,
                HeaderText = $"{(held.IsInbound ? "Inbound" : "Outgoing")} connection - {held.Protocol}",
                AppName = string.IsNullOrEmpty(held.ProcessName) ? $"Process {held.Pid}" : held.ProcessName,
                Path = held.AppPath,
                Pid = held.Pid,
                LocalEndpoint = $"{held.LocalIp}:{held.LocalPort}",
                RemoteEndpoint = $"{held.RemoteIp}:{held.RemotePort}",
                Icon = Icons.ForPath(held.AppPath)
            };
            PendingRequests.Add(item);
            ToastWindow.ShowToast("Blocker: Connection Request", $"{item.AppName} wants to connect to {item.RemoteEndpoint}", primaryLabel: "Review", isWarning: false);
            Report($"{item.AppName}: connection to {item.RemoteEndpoint} held awaiting your decision (Ask)");
        });
    }

    private void RefreshStatsIpStats()
    {
        IReadOnlyList<RemoteIpUsage> usage = _remoteIpHistory.GetRecent(7);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RemoteIpUsage u in usage)
        {
            seen.Add(u.RemoteIp);
            if (!_ipStatsMap.TryGetValue(u.RemoteIp, out EntityStatsRow? row))
            {
                row = new EntityStatsRow(u.RemoteIp, "Remote IP", 0, 0);
                _ipStatsMap[u.RemoteIp] = row;
                IpStats.Add(row);
            }

            row.BytesIn = u.BytesIn;
            row.BytesOut = u.BytesOut;
            row.FirstSeenUtc = u.FirstSeenUtc;
            row.LastSeenUtc = u.LastSeenUtc;
            row.ConnCount = u.ConnCount;
        }

        foreach (EntityStatsRow row in IpStats.Where(r => !seen.Contains(r.Name)).ToList())
        {
            IpStats.Remove(row);
            _ipStatsMap.Remove(row.Name);
        }
    }

    /// <summary>Real VirusTotal verdict for a selected process, when an API key is configured
    /// (Settings → Security). Falls back to the browser link otherwise.</summary>
    public async Task RefreshVtVerdictAsync(string? path)
    {
        string key = _settings.VirusTotalApiKey;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            VtChecked = true;
            VtVerdict = string.IsNullOrWhiteSpace(key)
                ? "No VirusTotal API key configured."
                : "File not found on disk.";
            return;
        }

        VtChecked = true;
        VtVerdict = "Scanning…";
        try
        {
            string sha = await Task.Run(() => VirusTotalService.Sha256Of(path));
            var report = await VirusTotalService.ScanAsync(sha, key);
            VtVerdict = report is null
                ? $"No VirusTotal report for {sha.Substring(0, 12)}…"
                : $"{report.Malicious}/{report.Total} engines flagged ({report.Harmless} harmless, {report.Undetected} undetected)";
        }
        catch (Exception ex)
        {
            VtVerdict = $"VirusTotal error: {ex.Message}";
        }
    }

    private readonly Dictionary<string, (BlockerState In, BlockerState Out, bool Blocked, CancellationTokenSource Cts)> _tempStates = new(StringComparer.Ordinal);

    /// <summary>3.04 "For a while" action: temporarily allow/deny, then revert to previous state.</summary>
    public Task TemporaryBlockAsync(ProcessRow row, bool deny)
    {
        string key = $"{row.Name.ToLowerInvariant()}|{row.Path}";
        if (_tempStates.TryGetValue(key, out var existing))
        {
            existing.Cts.Cancel();
            _tempStates.Remove(key);
        }

        AppRule rule = EnsureRule(row.Name, row.Path);
        var prior = (rule.BlockerIn, rule.BlockerOut, rule.Blocked);
        var cts = new CancellationTokenSource();
        _tempStates[key] = (deny ? BlockerState.Deny : BlockerState.Allow, deny ? BlockerState.Deny : BlockerState.Allow, deny, cts);

        if (deny)
        {
            rule.BlockerIn = BlockerState.Deny;
            rule.BlockerOut = BlockerState.Deny;
            rule.Blocked = true;
            row.IsBlocked = true;
            if (row.Pid > 0) Collector.KillConnections(row.Pid);
        }
        else
        {
            rule.BlockerIn = BlockerState.Allow;
            rule.BlockerOut = BlockerState.Allow;
            rule.Blocked = false;
            row.IsBlocked = false;
        }

        int minutes = Math.Max(1, _settings.TemporaryBlockMinutes);
        Report($"{row.Name}: {(deny ? "blocked" : "allowed")} for {minutes} minute(s)");
        _ = SyncFirewallAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), cts.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            await App.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (!_tempStates.TryGetValue(key, out var current) || current.Cts != cts)
                {
                    return; // A newer temporary action replaced this one.
                }

                _tempStates.Remove(key);
                AppRule r = EnsureRule(row.Name, row.Path);
                r.BlockerIn = prior.BlockerIn;
                r.BlockerOut = prior.BlockerOut;
                r.Blocked = prior.Blocked;
                row.IsBlocked = prior.Blocked;
                await SyncFirewallAsync();
                Persist();
                Report($"{row.Name}: temporary {(deny ? "block" : "allow")} expired, previous state restored");
            });
        });

        return Task.CompletedTask;
    }

    /// <summary>4.03 quick zone blocker: adds/removes a whole-zone Deny rule without opening the rule editor.</summary>
    public void QuickBlockZone(string zone, bool block)
    {
        if (_settings.Rules == null) _settings.Rules = new List<UnifiedRule>();
        UnifiedRule? existing = _settings.Rules.FirstOrDefault(r =>
            r.TargetType == RuleTargetType.Zone &&
            string.Equals(r.TargetValue, zone, StringComparison.OrdinalIgnoreCase) &&
            r.Type == RuleType.Blocker);

        if (block && existing is null)
        {
            _settings.Rules.Add(new UnifiedRule
            {
                Name = $"Quick block zone: {zone}",
                TargetType = RuleTargetType.Zone,
                TargetValue = zone,
                Type = RuleType.Blocker,
                BlockerAction = BlockerAction.Deny,
                Enabled = true,
                Weight = 1000,
            });
        }
        else if (!block && existing is not null)
        {
            _settings.Rules.Remove(existing);
        }

        _divertShaper.SetUnifiedRules(_settings.Rules);
        Persist();
        RefreshSelectedItemRules();
        AddConnectionLog("Zone " + zone, block ? "Blocked" : "Unblocked", "All", "-", "-", "-", block ? "Deny" : "Allow");
        Report($"Zone \"{zone}\" {(block ? "blocked" : "unblocked")}");
    }

    /// <summary>20.04: keep a Tag filter for every tag currently in use.</summary>
    public void SyncTagFilters()
    {
        if (_settings.Filters == null) _settings.Filters = new List<FilterRule>();
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        foreach (AppRule app in _settings.Apps)
        {
            if (string.IsNullOrWhiteSpace(app.Tags)) continue;
            foreach (string tag in app.Tags.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                used.Add(tag.Trim());
            }
        }

        bool changed = false;
        foreach (string tag in used)
        {
            bool exists = _settings.Filters.Any(f =>
                f.ConditionType == FilterConditionType.Tag &&
                string.Equals(f.Value, tag, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                _settings.Filters.Add(new FilterRule($"Tag: {tag}", FilterConditionType.Tag, tag));
                FilterRules.Add(new FilterRule($"Tag: {tag}", FilterConditionType.Tag, tag));
                changed = true;
            }
        }

        if (changed)
        {
            _store.Save(_settings);
            _divertShaper.SetFilters(_settings.Filters);
        ApplySettings();
        }
    }

    /// <summary>20.04: remove a tag from every app and delete its auto-created filter.</summary>
    public void DeleteTagFilter(string tag)
    {
        string trimmed = tag.Trim();
        foreach (AppRule app in _settings.Apps)
        {
            if (string.IsNullOrWhiteSpace(app.Tags)) continue;
            var kept = app.Tags.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !string.Equals(t.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Trim());
            app.Tags = string.Join(", ", kept);
        }

        FilterRule? toRemove = _settings.Filters.FirstOrDefault(f =>
            f.ConditionType == FilterConditionType.Tag &&
            string.Equals(f.Value, trimmed, StringComparison.OrdinalIgnoreCase));
        if (toRemove is not null)
        {
            _settings.Filters.Remove(toRemove);
            FilterRules.Remove(toRemove);
        }

        _store.Save(_settings);
        _divertShaper.SetFilters(_settings.Filters);
        ApplySettings();
        SyncTagFilters();
        Report($"Tag \"{trimmed}\" removed from all applications");
    }

    /// <summary>13.15/13.16: warn when the traffic DB grows beyond the configured limit or the disk is low.</summary>
    public void CheckDbHealth()
    {
        long dbBytes = _history.DatabaseSizeBytes();
        long limitBytes = Math.Max(1L, (long)_settings.StatsDbSizeLimitMB * 1024 * 1024);
        if (dbBytes > limitBytes)
        {
            ToastWindow.ShowToast("Traffic database is large", $"Traffic history is {dbBytes / (1024 * 1024):N0} MB, over the {_settings.StatsDbSizeLimitMB} MB warning limit. Open Settings → Traffic Database to repair or trim.", isWarning: true);
        }

        try
        {
            string root = _store.RootDirectory;
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(Path.GetFullPath(root)) ?? root);
            if (drive.IsReady && drive.AvailableFreeSpace < 256L * 1024 * 1024)
            {
                ToastWindow.ShowToast("Low disk space", $"Only {drive.AvailableFreeSpace / (1024 * 1024):N0} MB free on {drive.Name}. Valve traffic history may fail to save.", isWarning: true);
            }
        }
        catch (Exception)
        {
            // Drive probe is best-effort.
        }
    }

    public void SetQuota(RuleRow row, long bytes, int days)
    {
        if (!CheckControlPermission()) return;
        row.Rule.QuotaBytes = bytes;
        row.Rule.QuotaDays = days;
        RefreshQuotas();
        Persist();
        Status = bytes > 0 ? $"quota set for {row.App}" : $"quota cleared for {row.App}";
    }

    public void AddSchedule(string app, int kbps, int startMinute, int endMinute, bool[] days)
    {
        if (string.IsNullOrWhiteSpace(app) || kbps <= 0)
        {
            Status = "a schedule needs an application and a rate above zero";
            return;
        }

        string name = app.Trim().ToLowerInvariant();
        string path = FindRule(name)?.AppPath
            ?? _byPid.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Path
            ?? "";

        var schedule = new ScheduleRule
        {
            App = name,
            AppPath = path,
            LimitKbps = kbps,
            StartMinute = startMinute,
            EndMinute = endMinute,
            Days = days,
            Enabled = true,
        };

        _settings.Schedules.Add(schedule);
        var row = new ScheduleRow(schedule);
        row.Refresh();
        Schedules.Add(row);
        Persist();
        Status = $"{name} capped to {kbps} kbit/s during {schedule.Window}";
    }

    public void RemoveSchedule(ScheduleRow row)
    {
        _settings.Schedules.Remove(row.Rule);
        Schedules.Remove(row);
        Persist();
        Status = $"removed schedule for {row.App}";
    }

    /// <summary>Drops every rule for one app and puts the system back to defaults.</summary>
    public async Task ClearRuleAsync(RuleRow row)
    {
        if (!CheckControlPermission()) return;
        row.Rule.UploadLimitKbps = 0;
        row.Rule.Blocked = false;
        row.Rule.QuotaBytes = 0;
        row.Rule.Priority = TrafficPriority.Normal;
        SyncDriver();

        await _shaper.ClearAsync(row.Rule.AppPath);

        if (_byPid.Values.FirstOrDefault(p => string.Equals(p.Name, row.App, StringComparison.OrdinalIgnoreCase)) is { } live)
        {
            live.LimitKbps = 0;
            live.IsBlocked = false;
            live.Priority = TrafficPriority.Normal;
        }

        await SyncFirewallAsync();
        row.Refresh();
        Persist();
        Status = $"cleared rules for {row.App}";
    }

    /// <summary>Kill every live connection of an overflowing quota target (7.07.a).</summary>
    public void KillConnectionsForQuota(RuleRow row)
    {
        if (row == null) return;
        int killed = 0;
        foreach (ConnRow c in Connections.Where(c =>
                     string.Equals(c.ProcessName, row.App, StringComparison.OrdinalIgnoreCase) ||
                     c.Pid > 0 && string.Equals(c.ProcessName, row.App, StringComparison.OrdinalIgnoreCase)))
        {
            _divertShaper.KillConnection(c.LocalIp, c.LocalPort, c.RemoteIp, c.RemotePort);
            Collector.KillTcpConnection(c.LocalIp, c.LocalPort, c.RemoteIp, c.RemotePort);
            killed++;
        }

        var proc = _byPid.Values.FirstOrDefault(p => string.Equals(p.Name, row.App, StringComparison.OrdinalIgnoreCase));
        if (proc != null && proc.Pid > 0)
        {
            Collector.KillConnections(proc.Pid);
        }

        Status = killed > 0
            ? $"quota alert: killed {killed} connection(s) for {row.App}"
            : $"quota alert: no live connections for {row.App}";
        AddConnectionLog(row.App, "QuotaKill", "All", "-", "-", "-", "Quota overflow — killed");
    }

    /// <summary>Ignore the quota for a process: continue unmeasured and unblocked (7.07.b).</summary>
    public void IgnoreQuota(RuleRow row)
    {
        if (row == null) return;
        row.Rule.IgnoreQuota = true;
        row.Rule.Blocked = false;
        if (_byPid.Values.FirstOrDefault(p => string.Equals(p.Name, row.App, StringComparison.OrdinalIgnoreCase)) is { } live)
        {
            live.IsBlocked = false;
        }

        RefreshQuotas();
        _ = SyncFirewallAsync();
        Persist();
        row.Refresh();
        Status = $"quota ignored for {row.App} — traffic continues unmeasured";
    }

    /// <summary>Reset the used bytes of a quota so the app is unblocked again (7.07.c).</summary>
    public void ResetQuota(RuleRow row)
    {
        if (row == null) return;
        row.Rule.Blocked = false;
        if (_byPid.Values.FirstOrDefault(p => string.Equals(p.Name, row.App, StringComparison.OrdinalIgnoreCase)) is { } live)
        {
            live.IsBlocked = false;
        }

        _history.DeleteAppDay(row.App, DateOnly.FromDateTime(DateTime.Now));
        RefreshQuotas();
        _ = SyncFirewallAsync();
        Persist();
        row.Refresh();
        Status = $"quota reset for {row.App} — usage cleared and app unblocked";
    }

    /// <summary>Seven-day totals for the statistics tab, refreshed a few times a minute.</summary>
    private void RefreshStats()
    {
        IReadOnlyList<DailyUsage> week = _history.GetRange(7);
        Weekly.Clear();
        foreach (DailyUsage usage in week)
        {
            Weekly.Add(new StatsRow(usage.App, usage.BytesIn, usage.BytesOut));
        }

        RefreshStatsIpStats();
    }

    private void RefreshQuotas()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        IReadOnlyList<DailyUsage> usage = _history.GetDay(today);        var map = usage.ToDictionary(u => u.App, u => u, StringComparer.OrdinalIgnoreCase);

        foreach (RuleRow row in Rules)
        {
            if (map.TryGetValue(row.App, out DailyUsage? u))
            {
                row.SetUsage(u.BytesIn, u.BytesOut);
            }
            else
            {
                row.SetUsage(0, 0);
            }
        }

        foreach (AppRule rule in _settings.Apps.Where(r => r.QuotaBytes > 0 && !r.Blocked && !r.IgnoreQuota))
        {
            map.TryGetValue(rule.App, out DailyUsage? u);
            long used = (u?.BytesIn ?? 0) + (u?.BytesOut ?? 0);
            if (used >= rule.QuotaBytes)
            {
                rule.Blocked = true;
                if (_byPid.Values.FirstOrDefault(p => string.Equals(p.Name, rule.App, StringComparison.OrdinalIgnoreCase)) is { } row)
                {
                    row.IsBlocked = true;
                }

                Status = $"{rule.App} hit its quota — blocked";
                ToastWindow.ShowToast("Quota Exceeded", $"{rule.App} reached its quota ({Fmt.Bytes(used)} / {Fmt.Bytes(rule.QuotaBytes)}) and was blocked.", isWarning: true);
                _ = SyncFirewallAsync();
                Persist();
            }
        }
    }

    private async Task EnforceSchedulesAsync()
    {
        DateTime now = DateTime.Now;
        foreach (AppRule rule in _settings.Apps)
        {
            int effective = Policy.EffectiveLimit(rule.App, rule.UploadLimitKbps, _settings.Schedules, now);
            if (effective == rule.UploadLimitKbps)
            {
                continue;
            }

            await _shaper.SetUploadLimitAsync(rule.AppPath, effective);
        }
    }

    private async Task SyncAllAsync()
    {
        await SyncFirewallAsync();

        _ = _shaper.ClearAllAsync();
        foreach (AppRule rule in _settings.Apps.Where(r => r.UploadLimitEnabled && r.UploadLimitKbps > 0 && !_divertShaper.IsAvailable && !string.IsNullOrEmpty(r.AppPath)))
        {
            await _shaper.SetUploadLimitAsync(rule.AppPath, rule.UploadLimitKbps);
        }

        Status = "rules loaded";
    }

    private async Task SyncFirewallAsync()
    {
        if (!_firewall.IsAvailable)
        {
            return;
        }

        var desired = new List<FirewallRule>();
        if (_settings.BlockerEnabled)
        {
            bool defaultInDeny = string.Equals(_settings.DefaultInboundAction, "Deny", StringComparison.OrdinalIgnoreCase);
            bool defaultOutDeny = string.Equals(_settings.DefaultOutboundAction, "Deny", StringComparison.OrdinalIgnoreCase);

            foreach (AppRule app in _settings.Apps.Where(a => !string.IsNullOrEmpty(a.AppPath)))
            {
                BlockerState inState = app.BlockerIn != BlockerState.NotSet
                    ? app.BlockerIn
                    : (defaultInDeny ? BlockerState.Deny : BlockerState.Allow);

                BlockerState outState = app.BlockerOut != BlockerState.NotSet
                    ? app.BlockerOut
                    : (defaultOutDeny ? BlockerState.Deny : BlockerState.Allow);

                bool deny = app.Blocked
                    || inState == BlockerState.Deny
                    || outState == BlockerState.Deny;
                bool allow = !deny
                    && (inState == BlockerState.Allow || outState == BlockerState.Allow);

                if (!deny && !allow)
                {
                    continue;
                }

                desired.Add(new FirewallRule
                {
                    Id = StableId(app.App),
                    AppPath = app.AppPath,
                    Label = app.App,
                    Block = deny,
                    Enabled = true,
                });
            }
        }

        (bool ok, string message) = await _firewall.SyncAsync(desired);
        if (!ok)
        {
            Status = $"firewall: {message}";
        }
    }

    private static string StableId(string app)
    {
        byte[] hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("valve:" + app.ToLowerInvariant()));
        return new Guid(hash).ToString("N");
    }

    private void Persist() => _store.Save(_settings);

    public void RefreshNetworkAdapters()
    {
        try
        {
            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var nic in nics)
            {
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var existing = NetworkAdapters.FirstOrDefault(a => a.Id == nic.Id);
                if (existing is null)
                {
                    NetworkAdapters.Add(new NetworkAdapterRow(nic));
                }
                else
                {
                    existing.Update(nic, _collector.IntervalSeconds);
                }
            }
            Raise(nameof(ActiveAdapterSummary));
        }
        catch
        {
        }
    }

    public void AddUnifiedRule(UnifiedRule rule)
    {
        if (!CheckControlPermission()) return;
        if (rule == null) return;
        if (_settings.Rules == null) _settings.Rules = new List<UnifiedRule>();
        _settings.Rules.Add(rule);
        UnifiedRules.Add(rule);
        Persist();
        _divertShaper.SetUnifiedRules(_settings.Rules);
        RefreshSelectedItemRules();
        Report($"Rule '{rule.Name}' added.");
    }

    public void UpdateUnifiedRule(UnifiedRule rule)
    {
        if (!CheckControlPermission()) return;
        if (rule == null) return;
        if (_settings.Rules == null) _settings.Rules = new List<UnifiedRule>();
        var existing = _settings.Rules.FirstOrDefault(r => r.Id == rule.Id);
        if (existing != null)
        {
            int idx = _settings.Rules.IndexOf(existing);
            _settings.Rules[idx] = rule;
        }
        var obs = UnifiedRules.FirstOrDefault(r => r.Id == rule.Id);
        if (obs != null)
        {
            int idx = UnifiedRules.IndexOf(obs);
            UnifiedRules[idx] = rule;
        }
        Persist();
        _divertShaper.SetUnifiedRules(_settings.Rules);
        RefreshSelectedItemRules();
        Report($"Rule '{rule.Name}' updated.");
    }

    public void DeleteUnifiedRule(UnifiedRule rule)
    {
        if (!CheckControlPermission()) return;
        if (rule == null) return;
        if (_settings.Rules != null)
        {
            _settings.Rules.RemoveAll(r => r.Id == rule.Id);
        }
        UnifiedRules.Remove(rule);
        SelectedItemRules.Remove(rule);
        Persist();
        _divertShaper.SetUnifiedRules(_settings.Rules ?? new List<UnifiedRule>());
        RefreshSelectedItemRules();
        Report($"Rule '{rule.Name}' removed.");
    }

    public void ToggleUnifiedRule(UnifiedRule rule)
    {
        if (!CheckControlPermission()) return;
        if (rule == null) return;
        rule.IsEnabled = !rule.IsEnabled;
        Persist();
        _divertShaper.SetUnifiedRules(_settings.Rules);
        RefreshSelectedItemRules();
        Report($"Rule '{rule.Name}' toggled {(rule.IsEnabled ? "ON" : "OFF")}.");
    }

    public void RefreshSelectedItemRules()
    {
        SelectedItemRules.Clear();
        if (_selectedNode == null || _settings.Rules == null) return;

        if (_selectedNode.IsRoot)
        {
            foreach (var r in _settings.Rules.Where(r => r.TargetType == RuleTargetType.WholeComputer))
            {
                SelectedItemRules.Add(r);
            }
        }
        else if (_selectedNode.Key.StartsWith("zone:"))
        {
            string zone = _selectedNode.Key.Substring(5);
            foreach (var r in _settings.Rules.Where(r => r.TargetType == RuleTargetType.Zone && string.Equals(r.TargetZone, zone, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedItemRules.Add(r);
            }
        }
        else if (_selectedNode.Key.StartsWith("filter:"))
        {
            string filterId = _selectedNode.Key.Substring(7);
            var f = FilterRules.FirstOrDefault(x => x.Id == filterId);
            string filterName = f?.Name ?? "";
            foreach (var r in _settings.Rules.Where(r => r.TargetType == RuleTargetType.Filter && (r.TargetValue == filterId || string.Equals(r.TargetValue, filterName, StringComparison.OrdinalIgnoreCase))))
            {
                SelectedItemRules.Add(r);
            }
        }
        else if (SelectedProcess != null)
        {
            string appName = SelectedProcess.Name;
            string appPath = SelectedProcess.Path;
            foreach (var r in _settings.Rules.Where(r => r.TargetType == RuleTargetType.Application && (string.Equals(r.TargetValue, appName, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrEmpty(appPath) && string.Equals(r.TargetValue, appPath, StringComparison.OrdinalIgnoreCase)))))
            {
                SelectedItemRules.Add(r);
            }
        }
    }

    public FilterRule GetOrCreateAppFilter(string appName, string appPath)
    {
        var existing = FilterRules.FirstOrDefault(f => string.Equals(f.Name, $"App: {appName}", StringComparison.OrdinalIgnoreCase) || (f.Functions.Any(fn => fn.FunctionType == FilterFunctionType.Application && string.Equals(fn.Value, appName, StringComparison.OrdinalIgnoreCase))));
        if (existing != null) return existing;

        var newFilter = new FilterRule($"App: {appName}", FilterConditionType.Application, appName)
        {
            FilterType = FilterType.Normal,
            LimitType = LimitApplicationType.PerApplication
        };
        _settings.Filters.Add(newFilter);
        FilterRules.Add(newFilter);
        Persist();
        _divertShaper.SetFilters(_settings.Filters);
        ApplySettings();
        return newFilter;
    }

    public FilterRule CreateFilterFromSelection(ProcessRow? proc, ConnRow? conn)
    {
        string name = proc != null && conn != null ? $"{proc.Name} -> {conn.Remote}" : proc != null ? $"Filter: {proc.Name}" : conn != null ? $"Filter: {conn.Remote}" : "New Selection Filter";
        var rule = new FilterRule(name, FilterConditionType.Application, proc?.Name ?? "")
        {
            FilterType = FilterType.Normal,
            LimitType = LimitApplicationType.PerApplication
        };
        rule.Functions.Clear();
        if (proc != null)
        {
            rule.Functions.Add(new FilterFunction(FilterFunctionType.Application, FilterOperator.Is, proc.Name));
            if (!string.IsNullOrEmpty(proc.Path))
            {
                rule.Functions.Add(new FilterFunction(FilterFunctionType.ApplicationPath, FilterOperator.Is, proc.Path));
            }
        }
        if (conn != null)
        {
            if (!string.IsNullOrEmpty(conn.RemoteIp))
            {
                rule.Functions.Add(new FilterFunction(FilterFunctionType.RemoteAddress, FilterOperator.InRange, conn.RemoteIp));
            }
            if (conn.RemotePort > 0)
            {
                rule.Functions.Add(new FilterFunction(FilterFunctionType.RemotePort, FilterOperator.InRange, conn.RemotePort.ToString()));
            }
            if (!string.IsNullOrEmpty(conn.Proto))
            {
                rule.Functions.Add(new FilterFunction(FilterFunctionType.Protocol, FilterOperator.Is, conn.Proto));
            }
        }
        return rule;
    }

}
