using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valve.Core;

public enum LimitApplicationType
{
    PerApplication = 0,
    PerProcess = 1,
    PerConnection = 2
}

public enum ReactionSpeed
{
    Low = 0,
    Normal = 1,
    Fast = 2
}

/// <summary>Everything the user has configured about one application.</summary>
public sealed class AppRule
{
    public string AppPath { get; set; } = "";
    public string App { get; set; } = "";              // lowercase exe name, the join key
    public int UploadLimitKbps { get; set; }           // 0 = unlimited
    public int DownloadLimitKbps { get; set; }         // 0 = unlimited
    public bool UploadLimitEnabled { get; set; } = false;
    public bool DownloadLimitEnabled { get; set; } = false;
    public bool BothDirectionsCoupled { get; set; }
    public LimitApplicationType LimitType { get; set; } = LimitApplicationType.PerApplication;
    public TrafficPriority Priority { get; set; } = TrafficPriority.Normal;
    public bool Blocked { get; set; }
    public BlockerState BlockerIn { get; set; } = BlockerState.NotSet;
    public BlockerState BlockerOut { get; set; } = BlockerState.NotSet;
    public long QuotaBytes { get; set; }               // 0 = no quota
    public int QuotaDays { get; set; } = 1;

    public string Tags { get; set; } = "";
    public bool IgnoreTraffic { get; set; }            // Bypasses stats/history (VPN fix)
    public bool IgnoreLimits { get; set; }             // Bypasses shaper limits
    public bool IgnoreQuota { get; set; }              // 7.07.b: continue unmeasured after overflow

    [JsonIgnore]
    public bool HasQuota => QuotaBytes > 0;

    [JsonIgnore]
    public int EffectiveDownloadLimitKbps => (DownloadLimitEnabled && DownloadLimitKbps > 0) ? DownloadLimitKbps : 0;

    [JsonIgnore]
    public int EffectiveUploadLimitKbps => (UploadLimitEnabled && UploadLimitKbps > 0) ? UploadLimitKbps : 0;

    [JsonIgnore]
    public bool HasDownloadLimit => DownloadLimitKbps > 0;

    [JsonIgnore]
    public bool HasUploadLimit => UploadLimitKbps > 0;
}

/// <summary>A cap that applies only inside a time window.</summary>
public sealed class ScheduleRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string App { get; set; } = "";
    public string AppPath { get; set; } = "";
    public int LimitKbps { get; set; }
    public bool[] Days { get; set; } = { true, true, true, true, true, true, true }; // Sun..Sat
    public int StartMinute { get; set; } = 9 * 60;
    public int EndMinute { get; set; } = 17 * 60;
    public bool Enabled { get; set; } = true;

    public string Window => $"{StartMinute / 60:00}:{StartMinute % 60:00}–{EndMinute / 60:00}:{EndMinute % 60:00}";
}

public sealed class Settings
{
    public List<AppRule> Apps { get; set; } = new();
    public List<ScheduleRule> Schedules { get; set; } = new();
    public List<FilterRule> Filters { get; set; } = new();
    public List<UnifiedRule> Rules { get; set; } = new();
    public HashSet<string> HiddenApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ConnectionLimits { get; set; } = new();
    public Dictionary<string, int> ConnectionDownloadLimits { get; set; } = new();
    public Dictionary<string, TrafficPriority> ConnectionPriorities { get; set; } = new();
    public bool ConfirmOnExit { get; set; } = true;

    public double SampleIntervalSeconds { get; set; } = 1.0;
    public bool UseBits { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool ResolveLocations { get; set; } = false; // Default false for privacy/security
    public bool BlockerEnabled { get; set; } = false;
    public bool LimiterEnabled { get; set; } = false;
    public bool PrioritiesEnabled { get; set; } = false;
    public string SelectedAdapterId { get; set; } = "";
    public string Theme { get; set; } = "Dark";
    public bool AutoScroll { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 0; // 0 = Keep forever (unlimited)
    public int StatsDbSizeLimitMB { get; set; } = 2048; // 13.15: warn when stats DB exceeds this size
    public int FirstDayOfMonth { get; set; } = 1;      // 26.06: day of month that starts a billing/stats month
    public int TemporaryBlockMinutes { get; set; } = 5; // 3.04: "For a while" temporary allow/deny duration
    public bool StartWithWindows { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public int RemotePort { get; set; } = 4045;              // 15.x: remote admin listen port
    public string RemoteToken { get; set; } = "";            // 15.x: shared auth token for remote admin
    public bool RemoteListen { get; set; }                   // 15.x: accept remote connections
    public string VirusTotalApiKey { get; set; } = "";       // 13.x: real VirusTotal API scan key (blank = browser lookup only)
    public List<PermissionEntry> Permissions { get; set; } = new(); // 15.x: remote command permissions
    public List<WindowsUserPermission> WindowsPermissions { get; set; } = WindowsAclManager.GetDefaultPermissions(); // 16.x: Windows user/group permissions
    public string UpdateCheckUrl { get; set; } = ""; // 28.12: configurable update release endpoint
    public bool EmergencyBlockAllTraffic { get; set; } = true;
    public string DefaultBlockerAction { get; set; } = "Allow";
    public string DefaultInboundAction { get; set; } = "Allow";
    public string DefaultOutboundAction { get; set; } = "Allow";
    public double UiScale { get; set; } = 1.0;
    public double FontScale { get; set; } = 1.0;
    public bool ShowFullPath { get; set; } = false;
    public bool DualYAxis { get; set; } = true;

    // Activity View Columns (Matching NetLimiter default: 9 standard columns visible)
    public bool ShowColName { get; set; } = true;
    public bool ShowColDlRate { get; set; } = true;
    public bool ShowColUlRate { get; set; } = true;
    public bool ShowColDlLimit { get; set; } = true;
    public bool ShowColUlLimit { get; set; } = true;
    public bool ShowColDlTotal { get; set; } = true;
    public bool ShowColUlTotal { get; set; } = true;
    public bool ShowColInFw { get; set; } = true;
    public bool ShowColOutFw { get; set; } = true;
    public bool ShowColStatus { get; set; } = false;
    public bool AutoRefreshEnabled { get; set; } = true;

    // Global Machine Limits (1.05)
    public int GlobalDownloadLimitKbps { get; set; } = 0;
    public bool GlobalDownloadLimitEnabled { get; set; } = false;
    public int GlobalUploadLimitKbps { get; set; } = 0;
    public bool GlobalUploadLimitEnabled { get; set; } = false;

    // Global Line Speed (1.13, 2.05)
    public long GlobalDownloadLineSpeedBps { get; set; } = 12500000; // 100 Mbps default
    public long GlobalUploadLineSpeedBps { get; set; } = 2500000;     // 20 Mbps default
    public bool AutoDetectLineSpeed { get; set; } = true;

    // Priority Engine Tuning (2.07, 2.08, 2.09, 2.11)
    public ReactionSpeed PriorityReactionSpeed { get; set; } = ReactionSpeed.Normal;
    public bool AdaptiveBandwidthAllocation { get; set; } = true;
    public bool UseReversedLimitsPriority { get; set; } = false;

    public double CriticalReservationPercent { get; set; } = 40.0;
    public double HighReservationPercent { get; set; } = 30.0;
    public double NormalReservationPercent { get; set; } = 20.0;
    public double LowReservationPercent { get; set; } = 10.0;
}

/// <summary>JSON persistence for user configuration, under %ProgramData%\Valve or %LOCALAPPDATA%\Valve.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string RootDirectory { get; }
    private readonly string _path;

    /// <summary>Compatibility path pointing to standard ProgramData\Locktime\NetLimiter (Item 26.04).</summary>
    public static string NetLimiterLegacyStorageDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Locktime", "NetLimiter");

    public static string GetDefaultStorageDirectory()
    {
        try
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(programData))
            {
                string candidate = Path.Combine(programData, "Valve");
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve");
    }

    public SettingsStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? GetDefaultStorageDirectory();
        _path = Path.Combine(RootDirectory, "settings.json");
    }

    public Settings Load()
    {
        try
        {
            string loadPath = _path;
            if (File.Exists(loadPath))
            {
                string? json = AtomicStorage.LoadAtomic(loadPath, out _);
                if (!string.IsNullOrEmpty(json))
                {
                    Settings? loaded = JsonSerializer.Deserialize<Settings>(json, Options);
                    if (loaded is not null)
                    {
                        return loaded;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Corrupt file falls back to defaults.
        }

        return new Settings();
    }

    public void Save(Settings settings)
    {
        string dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(settings, Options);
        AtomicStorage.SaveAtomic(_path, json);
    }
}
