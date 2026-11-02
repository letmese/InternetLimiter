using System.Windows.Media;

namespace Valve.Core;

/// <summary>
/// One row in the activity tree: machine, zones, app groups, processes, and active IP connections.
/// Flattened into a list so a DataGrid can render it while keeping the indented
/// hierarchy NetLimiter uses.
/// </summary>
public sealed class TreeNode : Observable
{
    private double _inRate;
    private double _outRate;
    private bool _isExpanded;
    private bool _isOnline;
    private bool _canExpand;
    private int _children;

    public TreeNode(int level, string key, string name, string path, ProcessRow? process, ConnRow? connection = null)
    {
        Level = level;
        Key = key;
        Name = name;
        Path = path;
        Process = process;
        Connection = connection;
        Icon = connection is not null ? Icons.ConnectionIcon : Icons.ForPath(process?.Path);
        _isExpanded = (level == 0);
    }

    public ImageSource? Icon { get; }

    public int Level { get; }
    public string Key { get; }
    public string Name { get; }
    public string Path { get; }
    public ProcessRow? Process { get; }
    public ConnRow? Connection { get; }

    public bool IsConnection => Connection is not null;
    public string DirectionGlyph => Connection is not null ? (Connection.IsInbound ? "↙" : "↗") : "";
    public bool IsRoot => Level == 0;
    public bool IsAppOrGroup => Level == 1;
    public bool IsSubProcess => Level == 2 && Connection is null;

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) { Raise(nameof(ExpanderGlyph)); } }
    }

    public bool CanExpand
    {
        get => _canExpand;
        set { if (Set(ref _canExpand, value)) { Raise(nameof(ExpanderGlyph)); } }
    }

    public string ExpanderGlyph => CanExpand ? (_isExpanded ? "▾" : "▸") : "";
    public double Indent => Level * 16;

    public double InRate
    {
        get => _inRate;
        set { if (Set(ref _inRate, value)) { Raise(nameof(InRateText)); } }
    }

    public double OutRate
    {
        get => _outRate;
        set { if (Set(ref _outRate, value)) { Raise(nameof(OutRateText)); } }
    }

    public string InRateText => Fmt.Rate(_inRate);
    public string OutRateText => Fmt.Rate(_outRate);

    /// <summary>0..1, scaled against the busiest row so the bars stay comparable.</summary>
    public double InFraction { get; set; }
    public double OutFraction { get; set; }

    public bool IsOnline
    {
        get => _isOnline;
        set => Set(ref _isOnline, value);
    }

    public string Subtitle
    {
        get
        {
            if (Connection is not null)
            {
                return Connection.Location;
            }
            if (Process is null)
            {
                return $"{Children} item{(Children == 1 ? "" : "s")}";
            }
            return Process.Subtitle;
        }
    }

    public int Children
    {
        get => _children;
        set => Set(ref _children, value);
    }

    public bool Blocked => Process?.IsBlocked ?? false;
    public bool HasLimit => Connection is not null ? Connection.HasUploadLimit : (Process?.HasLimit ?? false);
    public bool HasDownloadLimit => Connection is not null ? Connection.HasDownloadLimit : (Process?.HasDownloadLimit ?? false);
    public bool HasUploadLimit => Connection is not null ? Connection.HasUploadLimit : (Process?.HasUploadLimit ?? false);
    public int LimitKbps => Connection is not null ? Connection.LimitKbps : (Process?.LimitKbps ?? 0);
    public int DownloadLimitKbps => Connection is not null ? Connection.DownloadLimitKbps : (Process?.DownloadLimitKbps ?? 0);
    public TrafficPriority Priority => Connection is not null ? Connection.Priority : (Process?.Priority ?? TrafficPriority.Normal);

    public bool IsDownloadLimitEnabled
    {
        get => Connection is not null ? Connection.IsDownloadLimitEnabled : (Process?.IsDownloadLimitEnabled ?? false);
        set
        {
            if (Connection is not null) Connection.IsDownloadLimitEnabled = value;
            else if (Process is not null) Process.IsDownloadLimitEnabled = value;
            Raise(nameof(IsDownloadLimitEnabled));
            Raise(nameof(DownloadLimitDisplay));
            Raise(nameof(DownloadLimitText));
        }
    }

    public bool IsUploadLimitEnabled
    {
        get => Connection is not null ? Connection.IsUploadLimitEnabled : (Process?.IsUploadLimitEnabled ?? false);
        set
        {
            if (Connection is not null) Connection.IsUploadLimitEnabled = value;
            else if (Process is not null) Process.IsUploadLimitEnabled = value;
            Raise(nameof(IsUploadLimitEnabled));
            Raise(nameof(UploadLimitDisplay));
            Raise(nameof(LimitText));
        }
    }

    public string DownloadLimitDisplay => Connection is not null ? Connection.DownloadLimitText : (Process?.DownloadLimitDisplay ?? "unlimited");
    public string UploadLimitDisplay => Connection is not null ? Connection.LimitText : (Process?.UploadLimitDisplay ?? "unlimited");

    public string PriorityBadgeText => Connection is not null ? Connection.PriorityText.ToUpperInvariant() : (Process?.PriorityBadgeText ?? "NORMAL");

    public Brush PriorityForegroundBrush => Process?.PriorityForegroundBrush ?? Brushes.DodgerBlue;
    public Brush PriorityBackgroundBrush => Process?.PriorityBackgroundBrush ?? new SolidColorBrush(Color.FromArgb(35, 88, 166, 255));
    public Brush PriorityBorderBrush => Process?.PriorityBorderBrush ?? new SolidColorBrush(Color.FromArgb(100, 88, 166, 255));

    public string LimitText => Connection is not null ? Connection.LimitText : (Process?.LimitText ?? "");
    public string DownloadLimitText => Connection is not null ? Connection.DownloadLimitText : (Process?.DownloadLimitText ?? "");
    public string PriorityText => Connection is not null ? Connection.PriorityText : (Process?.PriorityText ?? "");

    public bool IsNotConnection => Connection is null;
    public long? GroupInTotal { get; set; }
    public long? GroupOutTotal { get; set; }

    public string InTotalText => Connection is not null ? Connection.InTotalText : (GroupInTotal.HasValue ? Fmt.Bytes(GroupInTotal.Value) : (Process is not null ? Fmt.Bytes(Process.InTotal) : ""));
    public string OutTotalText => Connection is not null ? Connection.OutTotalText : (GroupOutTotal.HasValue ? Fmt.Bytes(GroupOutTotal.Value) : (Process is not null ? Fmt.Bytes(Process.OutTotal) : ""));

    public void RefreshDerived()
    {
        Raise(nameof(Blocked));
        Raise(nameof(HasLimit));
        Raise(nameof(HasDownloadLimit));
        Raise(nameof(HasUploadLimit));
        Raise(nameof(LimitKbps));
        Raise(nameof(DownloadLimitKbps));
        Raise(nameof(LimitText));
        Raise(nameof(DownloadLimitText));
        Raise(nameof(PriorityText));
        Raise(nameof(Subtitle));
        Raise(nameof(Children));
        Raise(nameof(CanExpand));
        Raise(nameof(ExpanderGlyph));
        Raise(nameof(IsConnection));
        Raise(nameof(DirectionGlyph));
        Raise(nameof(IsRoot));
        Raise(nameof(IsAppOrGroup));
        Raise(nameof(IsSubProcess));
        Raise(nameof(IsNotConnection));
        Raise(nameof(InTotalText));
        Raise(nameof(OutTotalText));
        Raise(nameof(InRateText));
        Raise(nameof(OutRateText));
    }
}
