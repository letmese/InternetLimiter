using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Valve.Core;

public readonly record struct GraphSample(double In, double Out);

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public readonly record struct ConnReading(
    string Key,
    int Pid,
    string Proto,
    string Local,
    string Remote,
    string RemoteIp,
    double InRate,
    double OutRate,
    long InTotal,
    long OutTotal,
    ushort LocalPort = 0,
    bool IsInbound = false,
    uint RttMs = 0,
    ulong RetransBytes = 0,
    bool IsForwarded = false);

public sealed record TrafficSample(
    DateTime At,
    IReadOnlyList<ConnReading> Connections,
    bool Elevated,
    string? Note);

public sealed record FileMetadata(string Name, string Product, string Version, string Company);

public static class FileMetadataService
{
    private static readonly Dictionary<string, FileMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static FileMetadata GetMetadata(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            string fallbackName = string.IsNullOrEmpty(exePath) ? "" : System.IO.Path.GetFileNameWithoutExtension(exePath);
            return new FileMetadata(fallbackName, "", "", "");
        }

        lock (_cache)
        {
            if (_cache.TryGetValue(exePath, out var cached))
            {
                return cached;
            }

            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                var meta = new FileMetadata(
                    Name: !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : System.IO.Path.GetFileName(exePath),
                    Product: info.ProductName ?? "",
                    Version: info.ProductVersion ?? info.FileVersion ?? "",
                    Company: info.CompanyName ?? "");
                _cache[exePath] = meta;
                return meta;
            }
            catch
            {
                var fallback = new FileMetadata(System.IO.Path.GetFileName(exePath), "", "", "");
                _cache[exePath] = fallback;
                return fallback;
            }
        }
    }
}

/// <summary>One row in the process table. Mutable so the UI updates in place.</summary>
public sealed class ProcessRow : Observable
{
    private double _inRate;
    private double _outRate;
    private long _inTotal;
    private long _outTotal;
    private int _connections;
    private int _limitKbps;
    private int _downloadLimitKbps;
    private bool _blocked;
    private TrafficPriority _priority = TrafficPriority.Normal;
    private bool _isHidden;
    private FileMetadata? _metadata;
    private ServiceInfo? _serviceInfo;

    public ProcessRow(int pid, string name, string path)
    {
        Pid = pid;
        Name = name;
        Path = path;
    }

    public int Pid { get; }
    public string Name { get; }
    public string Path { get; }
    public System.Windows.Media.ImageSource? Icon => Icons.ForPath(Path);

    public FileMetadata Metadata => _metadata ??= FileMetadataService.GetMetadata(Path);
    public string ProductName => Metadata.Product;
    public string ProductVersion => Metadata.Version;
    public string CompanyName => Metadata.Company;
    public string FileDescription => Metadata.Name;

    public ServiceInfo? ServiceInfo => _serviceInfo ??= ServiceResolver.GetServiceForPid(Pid);
    public string? ServiceName => ServiceInfo?.ServiceName;
    public string? ServiceDisplayName => ServiceInfo?.DisplayName;

    public bool IsSystemService => (ServiceInfo is not null) || (!string.IsNullOrEmpty(Path) && (Path.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) || Path.Contains(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase)));
    public bool IsStoreApp => !string.IsNullOrEmpty(Path) && Path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
    public string AppTypeBadge => !string.IsNullOrEmpty(ServiceDisplayName) ? $"⚙ {ServiceDisplayName}" : (IsSystemService ? "⚙ SYS" : (IsStoreApp ? "🛍 UWP" : ""));
    public string DisplayName => Fmt.ShowFullPath ? (string.IsNullOrEmpty(Path) ? Name : Path) : (!string.IsNullOrEmpty(ServiceDisplayName) ? $"{Name} ({ServiceDisplayName})" : Name);

    public List<GraphSample> GraphHistory { get; } = new(180);

    public void AddGraphSample(double inRate, double outRate)
    {
        lock (GraphHistory)
        {
            if (GraphHistory.Count >= 180)
            {
                GraphHistory.RemoveAt(0);
            }
            GraphHistory.Add(new GraphSample(inRate, outRate));
        }
    }

    public bool IsHidden { get => _isHidden; set => Set(ref _isHidden, value); }

    private DateTime _lastActiveUtc = DateTime.UtcNow;
    private bool _isAlive = true;
    private bool _hasEverHadTraffic;

    public DateTime LastActiveUtc { get => _lastActiveUtc; set => _lastActiveUtc = value; }
    public bool IsAlive { get => _isAlive; set { if (Set(ref _isAlive, value)) Raise(nameof(IsOnline)); } }
    public bool HasEverHadTraffic { get => _hasEverHadTraffic; set => _hasEverHadTraffic = value; }

    public bool IsOnline => (_inRate + _outRate > 0)
        || (_connections > 0)
        || (_isAlive && _hasEverHadTraffic && (DateTime.UtcNow - _lastActiveUtc).TotalSeconds < 120)
        || ((DateTime.UtcNow - _lastActiveUtc).TotalSeconds < 60);

    public double InRate
    {
        get => _inRate;
        set
        {
            if (value > 0) { _lastActiveUtc = DateTime.UtcNow; _hasEverHadTraffic = true; }
            if (Set(ref _inRate, value))
            {
                Raise(nameof(InRateText));
                Raise(nameof(InBar));
                Raise(nameof(DownloadUtilizationPercent));
                Raise(nameof(IsOnline));
            }
        }
    }

    public double OutRate
    {
        get => _outRate;
        set
        {
            if (value > 0) { _lastActiveUtc = DateTime.UtcNow; _hasEverHadTraffic = true; }
            if (Set(ref _outRate, value))
            {
                Raise(nameof(OutRateText));
                Raise(nameof(OutBar));
                Raise(nameof(UploadUtilizationPercent));
                Raise(nameof(IsOnline));
            }
        }
    }

    public long InTotal { get => _inTotal; set { if (Set(ref _inTotal, value)) { Raise(nameof(TotalText)); Raise(nameof(InTotalText)); } } }
    public long OutTotal { get => _outTotal; set { if (Set(ref _outTotal, value)) { Raise(nameof(TotalText)); Raise(nameof(OutTotalText)); } } }
    public string InTotalText => Fmt.Bytes(InTotal);
    public string OutTotalText => Fmt.Bytes(OutTotal);

    public int Connections
    {
        get => _connections;
        set
        {
            if (value > 0) _lastActiveUtc = DateTime.UtcNow;
            if (Set(ref _connections, value))
            {
                Raise(nameof(IsOnline));
                Raise(nameof(Subtitle));
            }
        }
    }

    private bool _downloadLimitEnabled = false;
    private bool _uploadLimitEnabled = false;

    public bool DownloadLimitEnabled
    {
        get => _downloadLimitEnabled;
        set
        {
            if (Set(ref _downloadLimitEnabled, value))
            {
                Raise(nameof(IsDownloadLimitEnabled));
                Raise(nameof(HasDownloadLimit));
                Raise(nameof(DownloadLimitDisplay));
                Raise(nameof(DownloadLimitText));
            }
        }
    }

    public bool IsDownloadLimitEnabled
    {
        get => _downloadLimitEnabled;
        set => DownloadLimitEnabled = value;
    }

    public bool UploadLimitEnabled
    {
        get => _uploadLimitEnabled;
        set
        {
            if (Set(ref _uploadLimitEnabled, value))
            {
                Raise(nameof(IsUploadLimitEnabled));
                Raise(nameof(HasUploadLimit));
                Raise(nameof(HasLimit));
                Raise(nameof(UploadLimitDisplay));
                Raise(nameof(LimitText));
            }
        }
    }

    public bool IsUploadLimitEnabled
    {
        get => _uploadLimitEnabled;
        set => UploadLimitEnabled = value;
    }

    public int UploadLimitKbps
    {
        get => _limitKbps;
        set => LimitKbps = value;
    }

    public int EffectiveDownloadLimitKbps => (_downloadLimitEnabled && _downloadLimitKbps > 0) ? _downloadLimitKbps : 0;
    public int EffectiveUploadLimitKbps => (_uploadLimitEnabled && _limitKbps > 0) ? _limitKbps : 0;

    public bool HasDownloadLimit => _downloadLimitKbps > 0 && _downloadLimitEnabled;
    public bool HasUploadLimit => _limitKbps > 0 && _uploadLimitEnabled;

    public string DownloadLimitDisplay => _downloadLimitKbps > 0
        ? (_downloadLimitEnabled ? $"{_downloadLimitKbps} kbit/s" : $"{_downloadLimitKbps} kbit/s (off)")
        : "unlimited";

    public string UploadLimitDisplay => _limitKbps > 0
        ? (_uploadLimitEnabled ? $"{_limitKbps} kbit/s" : $"{_limitKbps} kbit/s (off)")
        : "unlimited";

    public int LimitKbps
    {
        get => _limitKbps;
        set { if (Set(ref _limitKbps, value)) { if (value > 0) _uploadLimitEnabled = true; Raise(nameof(HasLimit)); Raise(nameof(HasUploadLimit)); Raise(nameof(LimitText)); Raise(nameof(UploadLimitDisplay)); Raise(nameof(UploadUtilizationPercent)); Raise(nameof(IsUploadLimitEnabled)); } }
    }

    /// <summary>Download cap in kbit/s. 0 means unlimited. Driver-only.</summary>
    public int DownloadLimitKbps
    {
        get => _downloadLimitKbps;
        set { if (Set(ref _downloadLimitKbps, value)) { if (value > 0) _downloadLimitEnabled = true; Raise(nameof(HasDownloadLimit)); Raise(nameof(DownloadLimitText)); Raise(nameof(DownloadLimitDisplay)); Raise(nameof(DownloadUtilizationPercent)); Raise(nameof(IsDownloadLimitEnabled)); } }
    }

    public string DownloadLimitText => _downloadLimitKbps > 0
        ? (_downloadLimitEnabled ? $"{_downloadLimitKbps} kbit/s" : $"{_downloadLimitKbps} kbit/s (off)")
        : "unlimited";

    public bool IsBlocked { get => _blocked; set => Set(ref _blocked, value); }

    public TrafficPriority Priority
    {
        get => _priority;
        set
        {
            if (Set(ref _priority, value))
            {
                Raise(nameof(PriorityText));
                Raise(nameof(PriorityBadgeText));
                Raise(nameof(PriorityForegroundBrush));
                Raise(nameof(PriorityBackgroundBrush));
                Raise(nameof(PriorityBorderBrush));
            }
        }
    }

    public string PriorityBadgeText => _priority switch
    {
        TrafficPriority.Critical => "CRITICAL",
        TrafficPriority.High => "HIGH",
        TrafficPriority.Low => "LOW",
        _ => "NORMAL",
    };

    public string PriorityText => _priority switch
    {
        TrafficPriority.Critical => "critical",
        TrafficPriority.High => "high",
        TrafficPriority.Low => "low",
        _ => "normal",
    };

    public System.Windows.Media.Brush PriorityForegroundBrush => _priority switch
    {
        TrafficPriority.Critical => System.Windows.Media.Brushes.Crimson,
        TrafficPriority.High => System.Windows.Media.Brushes.Goldenrod,
        TrafficPriority.Low => System.Windows.Media.Brushes.Gray,
        _ => System.Windows.Media.Brushes.DodgerBlue,
    };

    public System.Windows.Media.Brush PriorityBackgroundBrush => _priority switch
    {
        TrafficPriority.Critical => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 85, 85)),
        TrafficPriority.High => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 184, 108)),
        TrafficPriority.Low => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 139, 148, 158)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(35, 88, 166, 255)),
    };

    public System.Windows.Media.Brush PriorityBorderBrush => _priority switch
    {
        TrafficPriority.Critical => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 255, 85, 85)),
        TrafficPriority.High => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 255, 184, 108)),
        TrafficPriority.Low => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 139, 148, 158)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(100, 88, 166, 255)),
    };

    public bool HasLimit => _limitKbps > 0 && _uploadLimitEnabled;
    public string LimitText => _limitKbps > 0
        ? (_uploadLimitEnabled ? $"{_limitKbps} kbit/s" : $"{_limitKbps} kbit/s (off)")
        : string.Empty;
    public string InRateText => Fmt.Rate(_inRate);
    public string OutRateText => Fmt.Rate(_outRate);
    public string TotalText => $"↓{Fmt.Bytes(_inTotal)}  ↑{Fmt.Bytes(_outTotal)}";
    public string Subtitle => $"pid {Pid} · {_connections} conn{(_connections == 1 ? "" : "s")}";

    public double InBar { get; set; }
    public double OutBar { get; set; }

    /// <summary>Live utilization of the download cap, 0-100 (bytes/s → kbit/s). 0 when unlimited.</summary>
    public double DownloadUtilizationPercent => _downloadLimitKbps > 0
        ? Math.Min(100, ((_inRate * 8.0 / 1000.0) / _downloadLimitKbps) * 100)
        : 0;

    /// <summary>Live utilization of the upload cap, 0-100 (bytes/s → kbit/s). 0 when unlimited.</summary>
    public double UploadUtilizationPercent => _limitKbps > 0
        ? Math.Min(100, ((_outRate * 8.0 / 1000.0) / _limitKbps) * 100)
        : 0;

    private string _tags = "";
    private bool _ignoreTraffic;
    private bool _ignoreLimits;

    public string Tags { get => _tags; set { if (Set(ref _tags, value)) { Raise(nameof(TagsText)); } } }
    public string TagsText => string.IsNullOrWhiteSpace(_tags) ? "None" : _tags;
    public bool IgnoreTraffic { get => _ignoreTraffic; set => Set(ref _ignoreTraffic, value); }
    public bool IgnoreLimits { get => _ignoreLimits; set => Set(ref _ignoreLimits, value); }

    /// <summary>Re-emits the formatted strings, e.g. after the unit preference changes.</summary>
    public void ResetTotals()
    {
        _inTotal = 0;
        _outTotal = 0;
        Raise(nameof(TotalText));
    }

    public void RefreshText()
    {
        Raise(nameof(DisplayName));
        Raise(nameof(InRateText));
        Raise(nameof(OutRateText));
        Raise(nameof(TotalText));
        Raise(nameof(LimitText));
        Raise(nameof(PriorityText));
        Raise(nameof(IsOnline));
    }
}

public sealed class ConnRow : Observable
{
    private double _inRate;
    private double _outRate;
    private long _inTotal;
    private long _outTotal;
    private string _location = "…";
    private string _hostname = "";
    private uint _rttMs;
    private ulong _retransBytes;
    private int _limitKbps;

    public required string Key { get; init; }
    public required int Pid { get; init; }
    public required string Process { get; init; }
    public required string Proto { get; init; }
    public required string Local { get; init; }
    public required string Remote { get; init; }
    public required string RemoteIp { get; init; }
    public bool IsInbound { get; set; }

    /// <summary>True when the local endpoint is not an address owned by this host —
    /// the connection is transit traffic being forwarded/routed through the machine.</summary>
    public bool IsForwarded { get; set; }

    public string DirectionBadge => IsInbound ? "↓ IN" : "↑ OUT";

    public string ForwardedBadge => IsForwarded ? "⇄ FWD" : "";

    private int _downloadLimitKbps;
    private bool _downloadLimitEnabled = false;
    private bool _uploadLimitEnabled = false;
    private TrafficPriority _priority = TrafficPriority.Normal;

    public int DownloadLimitKbps
    {
        get => _downloadLimitKbps;
        set { if (Set(ref _downloadLimitKbps, value)) { if (value > 0) _downloadLimitEnabled = true; Raise(nameof(DownloadLimitText)); Raise(nameof(HasDownloadLimit)); Raise(nameof(DownloadLimitKBps)); Raise(nameof(IsDownloadLimitEnabled)); } }
    }

    public double DownloadLimitKBps
    {
        get => _downloadLimitKbps > 0 ? Math.Round(_downloadLimitKbps / 8.0, 1) : 0;
        set => DownloadLimitKbps = (int)Math.Round(value * 8.0);
    }

    public bool DownloadLimitEnabled
    {
        get => _downloadLimitEnabled;
        set { if (Set(ref _downloadLimitEnabled, value)) { Raise(nameof(IsDownloadLimitEnabled)); Raise(nameof(DownloadLimitText)); } }
    }

    public bool IsDownloadLimitEnabled { get => _downloadLimitEnabled; set => DownloadLimitEnabled = value; }

    public bool UploadLimitEnabled
    {
        get => _uploadLimitEnabled;
        set { if (Set(ref _uploadLimitEnabled, value)) { Raise(nameof(IsUploadLimitEnabled)); Raise(nameof(LimitText)); } }
    }

    public bool IsUploadLimitEnabled { get => _uploadLimitEnabled; set => UploadLimitEnabled = value; }

    public int UploadLimitKbps { get => LimitKbps; set => LimitKbps = value; }

    public int EffectiveDownloadLimitKbps => (_downloadLimitEnabled && _downloadLimitKbps > 0) ? _downloadLimitKbps : 0;
    public int EffectiveUploadLimitKbps => (_uploadLimitEnabled && _limitKbps > 0) ? _limitKbps : 0;

    public bool HasDownloadLimit => _downloadLimitKbps > 0 && _downloadLimitEnabled;
    public bool HasUploadLimit => _limitKbps > 0 && _uploadLimitEnabled;

    public string DownloadLimitText => _downloadLimitKbps > 0
        ? (_downloadLimitEnabled ? $"{_downloadLimitKbps} kbit/s" : $"{_downloadLimitKbps} kbit/s (off)")
        : "-";

    public TrafficPriority Priority
    {
        get => _priority;
        set { if (Set(ref _priority, value)) { Raise(nameof(PriorityText)); } }
    }

    public string PriorityText => _priority switch
    {
        TrafficPriority.Critical => "critical",
        TrafficPriority.High => "high",
        TrafficPriority.Low => "low",
        _ => "normal",
    };

    public int LimitKbps
    {
        get => _limitKbps;
        set { if (Set(ref _limitKbps, value)) { if (value > 0) _uploadLimitEnabled = true; Raise(nameof(LimitText)); Raise(nameof(HasUploadLimit)); Raise(nameof(LimitKBps)); Raise(nameof(IsUploadLimitEnabled)); } }
    }

    public double LimitKBps
    {
        get => _limitKbps > 0 ? Math.Round(_limitKbps / 8.0, 1) : 0;
        set => LimitKbps = (int)Math.Round(value * 8.0);
    }
    public string LimitText => _limitKbps > 0
        ? (_uploadLimitEnabled ? $"{_limitKbps} kbit/s" : $"{_limitKbps} kbit/s (off)")
        : "-";

    public string Location { get => _location; set => Set(ref _location, value); }
    public string Hostname { get => _hostname; set => Set(ref _hostname, value); }
    public uint RttMs { get => _rttMs; set { if (Set(ref _rttMs, value)) { Raise(nameof(RttText)); } } }
    public ulong RetransBytes { get => _retransBytes; set { if (Set(ref _retransBytes, value)) { Raise(nameof(RetransText)); } } }
    public string RttText => _rttMs > 0 ? $"{_rttMs} ms" : "< 1 ms";
    public string RetransText => _retransBytes > 0 ? Fmt.Bytes((long)_retransBytes) : "0 B";

    public double InRate { get => _inRate; set { if (Set(ref _inRate, value)) { Raise(nameof(InRateText)); } } }
    public double OutRate { get => _outRate; set { if (Set(ref _outRate, value)) { Raise(nameof(OutRateText)); } } }
    public string InRateText => Fmt.Rate(_inRate);
    public string OutRateText => Fmt.Rate(_outRate);

    public long InTotal { get => _inTotal; set { if (Set(ref _inTotal, value)) { Raise(nameof(InTotalText)); } } }
    public long OutTotal { get => _outTotal; set { if (Set(ref _outTotal, value)) { Raise(nameof(OutTotalText)); } } }
    public string InTotalText => Fmt.Bytes(_inTotal);
    public string OutTotalText => Fmt.Bytes(_outTotal);

    public List<GraphSample> GraphHistory { get; } = new(180);

    public void AddGraphSample(double inRate, double outRate)
    {
        lock (GraphHistory)
        {
            if (GraphHistory.Count >= 180)
            {
                GraphHistory.RemoveAt(0);
            }
            GraphHistory.Add(new GraphSample(inRate, outRate));
        }
    }

    public string ProcessName => Process;
    public string LocalIp => SplitEndpoint(Local).Ip;
    public int LocalPort => SplitEndpoint(Local).Port;
    public int RemotePort => SplitEndpoint(Remote).Port;

    public void Refresh() => RefreshText();

    private static (string Ip, int Port) SplitEndpoint(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint)) return ("0.0.0.0", 0);
        int colon = endpoint.LastIndexOf(':');
        if (colon > 0 && int.TryParse(endpoint[(colon + 1)..], out int port))
        {
            return (endpoint[..colon].Trim('[', ']'), port);
        }
        return (endpoint, 0);
    }

    public void RefreshText()
    {
        Raise(nameof(InRateText));
        Raise(nameof(OutRateText));
        Raise(nameof(InTotalText));
        Raise(nameof(OutTotalText));
        Raise(nameof(LimitText));
        Raise(nameof(DownloadLimitText));
        Raise(nameof(PriorityText));
        Raise(nameof(RttText));
        Raise(nameof(RetransText));
    }
}

/// <summary>
/// Persistent application catalog entry representing an application that has produced traffic (Section 21).
/// </summary>
public sealed class ApplicationEntry : Observable
{
    private long _totalBytesIn;
    private long _totalBytesOut;
    private DateTime _lastSeen = DateTime.Now;
    private string _tags = "";
    private bool _isHidden;

    public ApplicationEntry(string path, string name)
    {
        Path = path;
        Name = name;
    }

    public string Path { get; }
    public string Name { get; }
    public System.Windows.Media.ImageSource? Icon => Icons.ForPath(Path);

    public DateTime FirstSeen { get; set; } = DateTime.Now;
    public DateTime LastSeen { get => _lastSeen; set => Set(ref _lastSeen, value); }
    public string FirstSeenText => FirstSeen.ToString("yyyy-MM-dd HH:mm");
    public string LastSeenText => _lastSeen.ToString("yyyy-MM-dd HH:mm");

    public long TotalBytesIn
    {
        get => _totalBytesIn;
        set { if (Set(ref _totalBytesIn, value)) { Raise(nameof(TotalInText)); Raise(nameof(TotalBytesText)); } }
    }

    public long TotalBytesOut
    {
        get => _totalBytesOut;
        set { if (Set(ref _totalBytesOut, value)) { Raise(nameof(TotalOutText)); Raise(nameof(TotalBytesText)); } }
    }

    public string TotalInText => Fmt.Bytes(_totalBytesIn);
    public string TotalOutText => Fmt.Bytes(_totalBytesOut);
    public string TotalBytesText => Fmt.Bytes(_totalBytesIn + _totalBytesOut);

    public string Tags { get => _tags; set => Set(ref _tags, value); }
    public bool IsHidden { get => _isHidden; set => Set(ref _isHidden, value); }

    public bool ExistsOnDisk => !string.IsNullOrEmpty(Path) && File.Exists(Path);
    public string StatusText => ExistsOnDisk ? "Installed" : "Missing / Stale";

    public string Company => FileMetadataService.GetMetadata(Path).Company;
    public string Version => FileMetadataService.GetMetadata(Path).Version;
    public string Product => FileMetadataService.GetMetadata(Path).Product;

    private int _downloadLimitKbps;
    private int _uploadLimitKbps;
    private bool _downloadLimitEnabled = false;
    private bool _uploadLimitEnabled = false;
    private TrafficPriority _priority = TrafficPriority.Normal;

    public int DownloadLimitKbps
    {
        get => _downloadLimitKbps;
        set => Set(ref _downloadLimitKbps, value);
    }

    public int UploadLimitKbps
    {
        get => _uploadLimitKbps;
        set => Set(ref _uploadLimitKbps, value);
    }

    public bool DownloadLimitEnabled
    {
        get => _downloadLimitEnabled;
        set => Set(ref _downloadLimitEnabled, value);
    }

    public bool UploadLimitEnabled
    {
        get => _uploadLimitEnabled;
        set => Set(ref _uploadLimitEnabled, value);
    }

    public TrafficPriority Priority
    {
        get => _priority;
        set => Set(ref _priority, value);
    }

    public int EffectiveDownloadLimitKbps => (_downloadLimitEnabled && _downloadLimitKbps > 0) ? _downloadLimitKbps : 0;
    public int EffectiveUploadLimitKbps => (_uploadLimitEnabled && _uploadLimitKbps > 0) ? _uploadLimitKbps : 0;
}

public static class Fmt
{
    private static readonly string[] RateUnits = { "B/s", "KB/s", "MB/s", "GB/s" };
    private static readonly string[] BitRateUnits = { "b/s", "kbit/s", "Mbit/s", "Gbit/s" };
    private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB" };

    public enum UnitDisplayScale
    {
        Auto,
        Kilo,
        Mega
    }

    /// <summary>Global unit scaling mode: Auto / Kilo / Mega (22.06).</summary>
    public static UnitDisplayScale ScaleMode { get; set; } = UnitDisplayScale.Auto;

    /// <summary>When true, rates are shown in bits per second instead of bytes.</summary>
    public static bool UseBits { get; set; }

    /// <summary>When true, applications display full disk path instead of exe name.</summary>
    public static bool ShowFullPath { get; set; }

    public static string Rate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 0)
        {
            return UseBits ? "0 b/s" : "0 B/s";
        }

        if (UseBits)
        {
            return Scale(bytesPerSecond * 8, BitRateUnits);
        }

        return Scale(bytesPerSecond, RateUnits);
    }

    private static string Scale(double value, string[] units)
    {
        int unit = 0;
        if (ScaleMode == UnitDisplayScale.Kilo && units.Length > 1)
        {
            value /= 1024.0;
            unit = 1;
        }
        else if (ScaleMode == UnitDisplayScale.Mega && units.Length > 2)
        {
            value /= (1024.0 * 1024.0);
            unit = 2;
        }
        else
        {
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    public static string Bytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        int unit = 0;
        double value = bytes;
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {ByteUnits[unit]}");
    }

    public static string RatePair(double inRate, double outRate)
        => $"{Rate(inRate)} / {Rate(outRate)}";
}

/// <summary>Connection Log entry (Section 12 of NetLimiter feature set).</summary>
public sealed class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string TimeText => Timestamp.ToString("HH:mm:ss");
    public string App { get; set; } = "";
    public string Event { get; set; } = "Connected";
    public string Proto { get; set; } = "TCP";
    public string Local { get; set; } = "";
    public string Remote { get; set; } = "";
    public string Location { get; set; } = "";
    public string RuleAction { get; set; } = "Allow";
}

/// <summary>Entity statistics row for drill-down by IP, Port, or Protocol (Section 11 & 13).</summary>
public sealed class EntityStatsRow : Observable
{
    private long _bytesIn;
    private long _bytesOut;
    private DateTime _firstSeenUtc;
    private DateTime _lastSeenUtc;
    private int _connCount;

    public EntityStatsRow(string name, string category, long bytesIn, long bytesOut, DateTime? firstSeenUtc = null, DateTime? lastSeenUtc = null, int connCount = 0)
    {
        Name = name;
        Category = category;
        _bytesIn = bytesIn;
        _bytesOut = bytesOut;
        _firstSeenUtc = firstSeenUtc ?? DateTime.UtcNow;
        _lastSeenUtc = lastSeenUtc ?? DateTime.UtcNow;
        _connCount = connCount;
    }

    public string Name { get; }
    public string Category { get; }
    public long BytesIn { get => _bytesIn; set { if (Set(ref _bytesIn, value)) { Raise(nameof(InText)); Raise(nameof(TotalText)); } } }
    public long BytesOut { get => _bytesOut; set { if (Set(ref _bytesOut, value)) { Raise(nameof(OutText)); Raise(nameof(TotalText)); } } }
    public string InText => Fmt.Bytes(_bytesIn);
    public string OutText => Fmt.Bytes(_bytesOut);
    public string TotalText => Fmt.Bytes(_bytesIn + _bytesOut);

    public DateTime FirstSeenUtc { get => _firstSeenUtc; set { if (Set(ref _firstSeenUtc, value)) { Raise(nameof(FirstSeenText)); } } }
    public DateTime LastSeenUtc { get => _lastSeenUtc; set { if (Set(ref _lastSeenUtc, value)) { Raise(nameof(LastSeenText)); } } }
    public int ConnCount { get => _connCount; set { if (Set(ref _connCount, value)) { Raise(nameof(ConnCountText)); } } }
    public string FirstSeenText => _firstSeenUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string LastSeenText => _lastSeenUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string ConnCountText => _connCount.ToString();
}

// ============================================================================
// PHASE 3: UNIFIED RULE ENGINE MODELS (Sections 4 & 5)
// ============================================================================

public enum RuleType
{
    Limit = 0,
    Blocker = 1,
    Quota = 2,
    Priority = 3,
    Ignore = 4
}

public enum RuleDirection
{
    In = 0,
    Out = 1,
    Both = 2
}

public enum BlockerAction
{
    Allow = 0,
    Deny = 1,
    Ask = 2,
    NotSet = 3
}

public enum RuleTargetType
{
    WholeComputer = 0,
    Application = 1,
    Filter = 2,
    Zone = 3,
    Connection = 4
}

public enum IgnoreType
{
    IgnoreData = 0,
    IgnoreLimit = 1
}

public sealed class RuleScheduleEntry : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Action { get; set; } = "Start"; // "Start", "Stop", "Reset"
    public int MinuteOfDay { get; set; } = 9 * 60; // 09:00 default
    public bool[] Days { get; set; } = { true, true, true, true, true, true, true }; // Sun..Sat

    [JsonIgnore]
    public int TimeMinutes
    {
        get => MinuteOfDay;
        set => MinuteOfDay = value;
    }

    [JsonIgnore]
    public string TimeText => $"{MinuteOfDay / 60:00}:{MinuteOfDay % 60:00}";

    [JsonIgnore]
    public string ActionText => Action;

    [JsonIgnore]
    public string RecurrenceText => Days.All(d => d) ? "Every Day" : (Days.Skip(1).Take(5).All(d => d) && !Days[0] && !Days[6] ? "Weekdays" : "Custom Days");
}

public sealed class RuleHotKey
{
    public int HotKeyId { get; set; }
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }
    public string KeyDisplay { get; set; } = "";
}

public sealed class UnifiedRule : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "";
    private RuleTargetType _targetType = RuleTargetType.Application;
    private string _targetValue = "";
    private string _targetZone = "";
    private RuleType _type = RuleType.Limit;
    private RuleDirection _direction = RuleDirection.Both;
    private bool _isEnabled = true;
    private int _limitKbps;
    private LimitApplicationType _limitType = LimitApplicationType.PerApplication;
    private BlockerAction _blockerAction = BlockerAction.Deny;
    private int _weight = 3;
    private TrafficPriority _priorityLevel = TrafficPriority.Normal;
    private long _quotaBytes;
    private int _quotaPeriodDays = 1;
    private string _quotaExceededAction = "Block";
    private long _currentUsageIn;
    private long _currentUsageOut;
    private IgnoreType _ignoreMode = IgnoreType.IgnoreData;
    private string _hotKey = "";

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

    public RuleTargetType TargetType
    {
        get => _targetType;
        set { if (Set(ref _targetType, value)) { Raise(nameof(TargetDisplay)); } }
    }

    public string TargetValue
    {
        get => _targetValue;
        set { if (Set(ref _targetValue, value)) { Raise(nameof(TargetDisplay)); } }
    }

    public string TargetZone
    {
        get => _targetZone;
        set => Set(ref _targetZone, value);
    }

    public RuleType Type
    {
        get => _type;
        set { if (Set(ref _type, value)) { Raise(nameof(TypeBadge)); Raise(nameof(ValueSummary)); } }
    }

    public RuleDirection Direction
    {
        get => _direction;
        set { if (Set(ref _direction, value)) { Raise(nameof(DirectionText)); Raise(nameof(ValueSummary)); } }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    [JsonIgnore]
    public bool Enabled
    {
        get => _isEnabled;
        set => IsEnabled = value;
    }

    // Limit parameters
    public int LimitKbps
    {
        get => _limitKbps;
        set { if (Set(ref _limitKbps, value)) { Raise(nameof(ValueSummary)); } }
    }

    public LimitApplicationType LimitType
    {
        get => _limitType;
        set => Set(ref _limitType, value);
    }

    // Blocker parameters
    public BlockerAction BlockerAction
    {
        get => _blockerAction;
        set { if (Set(ref _blockerAction, value)) { Raise(nameof(ValueSummary)); } }
    }

    public int Weight
    {
        get => _weight;
        set { if (Set(ref _weight, value)) { Raise(nameof(ValueSummary)); } }
    }

    // Priority parameters
    public TrafficPriority PriorityLevel
    {
        get => _priorityLevel;
        set { if (Set(ref _priorityLevel, value)) { Raise(nameof(ValueSummary)); } }
    }

    // Quota parameters
    public long QuotaBytes
    {
        get => _quotaBytes;
        set { if (Set(ref _quotaBytes, value)) { Raise(nameof(ValueSummary)); } }
    }

    public int QuotaPeriodDays
    {
        get => _quotaPeriodDays;
        set => Set(ref _quotaPeriodDays, value);
    }

    public string QuotaExceededAction
    {
        get => _quotaExceededAction;
        set => Set(ref _quotaExceededAction, value);
    }

    public long CurrentUsageIn
    {
        get => _currentUsageIn;
        set { if (Set(ref _currentUsageIn, value)) { Raise(nameof(ValueSummary)); } }
    }

    public long CurrentUsageOut
    {
        get => _currentUsageOut;
        set { if (Set(ref _currentUsageOut, value)) { Raise(nameof(ValueSummary)); } }
    }

    public void AddUsage(long inBytes, long outBytes)
    {
        if (inBytes > 0) Interlocked.Add(ref _currentUsageIn, inBytes);
        if (outBytes > 0) Interlocked.Add(ref _currentUsageOut, outBytes);
    }

    // Ignore parameters
    public IgnoreType IgnoreMode
    {
        get => _ignoreMode;
        set { if (Set(ref _ignoreMode, value)) { Raise(nameof(ValueSummary)); } }
    }

    // Scheduler & HotKey
    public List<RuleScheduleEntry> Schedules { get; set; } = new();

    public string HotKey
    {
        get => _hotKey;
        set => Set(ref _hotKey, value);
    }

    public RuleHotKey? HotKeyConfig { get; set; }

    [JsonIgnore]
    public string TypeBadge => Type.ToString().ToUpperInvariant();

    [JsonIgnore]
    public string DirectionText => Direction switch
    {
        RuleDirection.In => "↓ In",
        RuleDirection.Out => "↑ Out",
        _ => "⇅ Both"
    };

    [JsonIgnore]
    public string TargetDisplay => TargetType switch
    {
        RuleTargetType.WholeComputer => "💻 Whole Computer",
        RuleTargetType.Zone => string.IsNullOrEmpty(TargetValue) ? "🌐 All Zones" : $"🌐 Zone: {TargetValue}",
        RuleTargetType.Filter => $"🔍 Filter: {TargetValue}",
        RuleTargetType.Connection => $"🔗 Conn: {TargetValue}",
        _ => string.IsNullOrEmpty(TargetValue) ? "Application" : TargetValue
    };

    [JsonIgnore]
    public string ScheduleSummary => Schedules.Count switch
    {
        0 => "-",
        1 => $"{Schedules[0].ActionText} {Schedules[0].TimeText} ({Schedules[0].RecurrenceText})",
        _ => $"{Schedules.Count} events"
    };

    [JsonIgnore]
    public string ValueSummary => Type switch
    {
        RuleType.Limit => $"{LimitKbps} kbit/s ({DirectionText})",
        RuleType.Blocker => $"{BlockerAction} (Weight {Weight})",
        RuleType.Quota => $"{Fmt.Bytes(CurrentUsageIn + CurrentUsageOut)} / {Fmt.Bytes(QuotaBytes)} ({QuotaPeriodDays}d)",
        RuleType.Priority => PriorityLevel.ToString().ToUpperInvariant(),
        RuleType.Ignore => IgnoreMode == IgnoreType.IgnoreData ? "Ignore Data (Bypass stats)" : "Ignore Limits",
        _ => "-"
    };
}

