using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using Valve.Interop;
using WindivertDotnet;

namespace Valve.Core;

/// <summary>
/// Direction of bandwidth shaping rules.
/// </summary>
public enum LimitDirection
{
    Inbound,
    Outbound,
    Both
}

/// <summary>
/// High-performance active inline network traffic shaper and monitor powered by the signed WinDivert kernel driver.
/// Enforces multi-tier hierarchical token-bucket limits, sub-millisecond low-CPU packet pacing,
/// 4-tier prioritized pacing scheduler with reverse limits, wire-speed fast-path bypass,
/// and connection teardown via Native.SetTcpEntry and synthetic TCP RST injection.
/// </summary>
public sealed class WinDivertShaper : IDisposable
{
    // Direction-aware lock-free O(1) port-to-PID lookup tables (double-buffered to prevent race with Array.Clear)
    private int[] _tcpPortOwners = new int[65536];
    private int[] _udpPortOwners = new int[65536];
    private int[] _scratchTcpPortOwners = new int[65536];
    private int[] _scratchUdpPortOwners = new int[65536];
    private readonly ConcurrentDictionary<ushort, int> _portToPid = new();

    // Telemetry throughput accumulators
    private readonly ulong[] _fastBytesIn = new ulong[65536];
    private readonly ulong[] _fastBytesOut = new ulong[65536];
    private volatile bool _isSniffMode = true;
    public bool IsSniffMode => _isSniffMode;
    private readonly ConcurrentDictionary<int, ulong> _bytesIn = new();
    private readonly ConcurrentDictionary<int, ulong> _bytesOut = new();
    private readonly ConcurrentDictionary<int, DateTime> _pidLastSeen = new();
    private DateTime _lastStaleCleanupUtc = DateTime.UtcNow;
    private ulong _totalBytesIn;
    private ulong _totalBytesOut;

    // Heartbeat and diagnostic telemetry
    public DateTime LastPacketUtc { get; private set; } = DateTime.UtcNow;
    private long _totalPacketsReceived;
    private long _totalPacketsPidZero;
    public double Pid0Ratio
    {
        get
        {
            long total = Interlocked.Read(ref _totalPacketsReceived);
            return total > 0 ? (double)Interlocked.Read(ref _totalPacketsPidZero) / total : 0.0;
        }
    }
    private long _tryGetStatsCalls;
    private long _tryGetStatsHits;
    public double TryGetStatsHitRatio
    {
        get
        {
            long calls = Interlocked.Read(ref _tryGetStatsCalls);
            return calls > 0 ? (double)Interlocked.Read(ref _tryGetStatsHits) / calls : 0.0;
        }
    }

    // Token Buckets for hierarchical rate limiting
    private TokenBucket? _globalInBucket;
    private TokenBucket? _globalOutBucket;
    private readonly ConcurrentDictionary<int, TokenBucket> _processInBuckets = new();
    private readonly ConcurrentDictionary<int, TokenBucket> _processOutBuckets = new();
    private readonly ConcurrentDictionary<string, TokenBucket> _appInBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TokenBucket> _appOutBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TokenBucket> _connInBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TokenBucket> _connOutBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TokenBucket> _filterInBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TokenBucket> _filterOutBuckets = new(StringComparer.OrdinalIgnoreCase);

    // Active limit rates in kbit/s (for fast lookup and UI state)
    private readonly ConcurrentDictionary<int, int> _processDlLimits = new();
    private readonly ConcurrentDictionary<int, int> _processUlLimits = new();
    private readonly ConcurrentDictionary<string, int> _appDlLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _appUlLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _connDlLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _connUlLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _filterDlLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _filterUlLimits = new(StringComparer.OrdinalIgnoreCase);

    // Active Unified Rules & Filters (Phase 3)
    private volatile List<UnifiedRule> _unifiedRules = new();
    private volatile List<FilterRule> _filters = new();
    private readonly ConcurrentDictionary<int, string> _pidToAppPath = new();
    public DnsResolver? DnsResolver { get; set; }

    private readonly struct FlowCacheEntry
    {
        public readonly RuleEvaluationResult Result;
        public readonly string AppName;
        public readonly string AppPath;
        public readonly string LocalIp;
        public readonly string RemoteIp;
        public readonly bool IsLocal;
        public readonly long Timestamp;

        public FlowCacheEntry(RuleEvaluationResult result, string appName, string appPath, string localIp, string remoteIp, bool isLocal, long timestamp)
        {
            Result = result;
            AppName = appName;
            AppPath = appPath;
            LocalIp = localIp;
            RemoteIp = remoteIp;
            IsLocal = isLocal;
            Timestamp = timestamp;
        }
    }
    private readonly ConcurrentDictionary<ulong, FlowCacheEntry> _flowCache = new();
    private static readonly Func<int, ulong, ulong> AddLenFactory = static (_, len) => len;
    private static readonly Func<int, ulong, ulong, ulong> UpdateLenFactory = static (_, old, len) => old + len;

    public void SetUnifiedRules(IEnumerable<UnifiedRule> rules) { _unifiedRules = rules?.ToList() ?? new(); _flowCache.Clear(); UpdateEngineState(); }
    public void SetFilters(IEnumerable<FilterRule> filters) { _filters = filters?.ToList() ?? new(); _flowCache.Clear(); UpdateEngineState(); }
    public void SetProcessPath(int pid, string appPath)
    {
        if (pid > 0 && !string.IsNullOrEmpty(appPath))
        {
            _pidToAppPath[pid] = appPath;
        }
    }

    // Priorities
    private readonly ConcurrentDictionary<int, TrafficPriority> _processPriorities = new();
    private readonly ConcurrentDictionary<string, TrafficPriority> _appPriorities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrafficPriority> _connPriorities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrafficPriority> _filterPriorities = new(StringComparer.OrdinalIgnoreCase);

    // Blocker "Ask" Packet Holding (Items 3.02, 3.03, 3.04)
    private readonly ConcurrentDictionary<ulong, HeldPacket> _heldPackets = new();
    private readonly ConcurrentDictionary<string, BlockerAction> _heldDecisions = new(StringComparer.OrdinalIgnoreCase);
    private long _nextHeldPacketId;
    private volatile string _defaultBlockerAction = "Allow";

    private volatile bool _isBlockerEnabled = false;
    public bool IsBlockerEnabled
    {
        get => _isBlockerEnabled;
        set
        {
            _isBlockerEnabled = value;
            UpdateEngineState();
        }
    }

    public string DefaultBlockerAction
    {
        get => _defaultBlockerAction;
        set => _defaultBlockerAction = value ?? "Allow";
    }

    public event Action<HeldPacket>? OnPacketHeld;
    public int HeldPacketsCount => _heldPackets.Count;

    // Master toggles
    private volatile bool _isLimiterEnabled = false;
    public bool IsLimiterEnabled
    {
        get => _isLimiterEnabled;
        set
        {
            _isLimiterEnabled = value;
            UpdateEngineState();
        }
    }

    private volatile bool _isPrioritiesEnabled = false;
    public bool IsPrioritiesEnabled
    {
        get => _isPrioritiesEnabled;
        set
        {
            _isPrioritiesEnabled = value;
            UpdateEngineState();
        }
    }

    public bool HasActivePriorities =>
        !_processPriorities.IsEmpty || !_appPriorities.IsEmpty ||
        !_connPriorities.IsEmpty || !_filterPriorities.IsEmpty;

    public bool HasActiveIndividualLimits =>
        !_appInBuckets.IsEmpty || !_appOutBuckets.IsEmpty ||
        !_connInBuckets.IsEmpty || !_connOutBuckets.IsEmpty ||
        !_processInBuckets.IsEmpty || !_processOutBuckets.IsEmpty ||
        !_filterInBuckets.IsEmpty || !_filterOutBuckets.IsEmpty;

    public bool HasActiveLimits => (IsLimiterEnabled && (_globalInBucket != null || _globalOutBucket != null)) || HasActiveIndividualLimits;

    public bool HasActiveUnifiedRules
    {
        get
        {
            var rules = _unifiedRules;
            if (rules == null || rules.Count == 0) return false;
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                if (!r.IsEnabled) continue;
                if (r.Type == RuleType.Ignore) return true;
                if (r.Type == RuleType.Quota) return true;
                if (r.Type == RuleType.Blocker && IsBlockerEnabled) return true;
                if (r.Type == RuleType.Limit && IsLimiterEnabled) return true;
                if (r.Type == RuleType.Priority && IsPrioritiesEnabled) return true;
            }
            return false;
        }
    }

    public bool NeedsPacketInterception =>
        (IsLimiterEnabled && (_globalInBucket != null || _globalOutBucket != null || HasActiveIndividualLimits)) ||
        HasActiveIndividualLimits ||
        IsBlockerEnabled ||
        (IsPrioritiesEnabled && HasActivePriorities) ||
        HasActiveUnifiedRules;

    private readonly object _engineLock = new();
    public bool IsEngineActive => _workerThread != null && !_stopping && !_disposed;

    // Line speeds & reservation percentages (Section 2)
    private long _downloadLineSpeedBps = 12500000; // 100 Mbps default
    private long _uploadLineSpeedBps = 2500000;     // 20 Mbps default
    public ReactionSpeed ReactionSpeed { get; set; } = ReactionSpeed.Normal;
    public bool AdaptiveAllocation { get; set; } = true;
    public bool UseReversedLimits { get; set; } = false;

    private double _criticalReservationPct = 40.0;
    private double _highReservationPct = 30.0;
    private double _normalReservationPct = 20.0;
    private double _lowReservationPct = 10.0;

    // Moving average traffic tracking per priority tier
    private double _smoothedCriticalInRate;
    private double _smoothedHighInRate;
    private double _smoothedCriticalOutRate;
    private double _smoothedHighOutRate;
    private long _lastRateCalcTicks;

    // Peak rate tracker for dynamic line speed detection (Item 2.05)
    private double _peakObservedInRate;
    private double _peakObservedOutRate;

    // Threads and driver synchronization
    private Thread? _workerThread;
    private Thread? _pacerThread;
    private WinDivert? _divert;
    private System.Threading.Timer? _portRefreshTimer;
    private readonly ManualResetEventSlim _pacingSignal = new(false);
    private volatile bool _stopping;
    private bool _available;
    private bool _disposed;
    private bool _timerPeriodActive;

    // Pacing queue: min-heap ordered by release timestamp
    private readonly PriorityQueue<PacedPacket, long> _pacingQueue = new();
    private readonly object _pacingLock = new();

    // Reusable packet pool (zero GC allocations under sustained throughput)
    private readonly ConcurrentBag<PacedPacket> _packetPool = new();
    private const int MaxPoolSize = 1024;
    private const double MaxQueueDelaySeconds = 0.200; // 200ms tail-drop threshold

    private static readonly Lazy<bool> _driverFilesPresent = new(() =>
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return File.Exists(Path.Combine(baseDir, "WinDivert.dll")) &&
               File.Exists(Path.Combine(baseDir, "WinDivert64.sys"));
    });

    public bool IsAvailable => _available && !_disposed;
    public string UnavailableReason { get; private set; } = "not started";

    public WinDivertShaper()
    {
        _lastRateCalcTicks = Stopwatch.GetTimestamp();
    }

    public void Start()
    {
        if (_disposed)
        {
            UnavailableReason = "disposed";
            return;
        }

        if (!_driverFilesPresent.Value)
        {
            _available = false;
            UnavailableReason = "driver files missing";
            return;
        }

        _available = true;
        UnavailableReason = "none";

        // Initial port mapping from OS TCP/UDP tables
        RefreshPortMap();

        // Refresh OS port-to-PID lookup every 1000ms
        _portRefreshTimer ??= new System.Threading.Timer(_ => RefreshPortMap(), null, 1000, 1000);

        UpdateEngineState();
    }

    private void UpdateEngineState()
    {
        if (_disposed) return;
        lock (_engineLock)
        {
            if (_disposed) return;
            bool targetSniff = !NeedsPacketInterception;

            // If already running in the desired mode, keep running
            if (_workerThread != null && _isSniffMode == targetSniff)
            {
                return;
            }

            // If switching modes, restart workers cleanly
            if (_workerThread != null)
            {
                StopWorkers();
            }

            StartWorkers(targetSniff);
        }
    }

    private void StartWorkers(bool sniffMode)
    {
        if (_disposed || _workerThread != null) return;
        if (!_driverFilesPresent.Value)
        {
            _available = false;
            UnavailableReason = "driver files missing";
            return;
        }

        try
        {
            _isSniffMode = sniffMode;
            var flag = sniffMode ? WinDivertFlag.Sniff : WinDivertFlag.None;

            if (!sniffMode && !_timerPeriodActive)
            {
                try
                {
                    if (Native.TimeBeginPeriod(1) == 0)
                    {
                        _timerPeriodActive = true;
                    }
                }
                catch { }
            }

            _divert = new WinDivert("!loopback and (tcp or udp)", WinDivertLayer.Network, 0, flag)
            {
                QueueLength = 16384,
                QueueSize = 32 * 1024 * 1024,
                QueueTime = TimeSpan.FromSeconds(2)
            };

            _stopping = false;
            _available = true;
            UnavailableReason = "none";
            LastPacketUtc = DateTime.UtcNow;

            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Normal,
                Name = sniffMode ? "WinDivert-SniffMonitor" : "WinDivert-ActivePacketRouter"
            };
            _workerThread.Start();

            if (!sniffMode)
            {
                _pacerThread = new Thread(PacerLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Normal,
                    Name = "WinDivert-PrecisionPacer"
                };
                _pacerThread.Start();
            }

            EventLogBridge.Info($"WinDivert engine started (mode: {(sniffMode ? "Sniff/0% CPU" : "Active Shaping")}).");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start WinDivert shaper: {ex.Message}");
            _available = false;
            UnavailableReason = $"handle open failed: {ex.Message}";
        }
    }

    private void StopWorkers()
    {
        _stopping = true;
        try { _divert?.Dispose(); } catch { }
        _divert = null;

        _pacingSignal.Set();

        try
        {
            if (_workerThread != null && _workerThread.IsAlive)
            {
                _workerThread.Join(300);
            }
        }
        catch { }
        _workerThread = null;

        try
        {
            if (_pacerThread != null && _pacerThread.IsAlive)
            {
                _pacerThread.Join(300);
            }
        }
        catch { }
        _pacerThread = null;

        lock (_pacingLock)
        {
            while (_pacingQueue.Count > 0)
            {
                var p = _pacingQueue.Dequeue();
                if (_packetPool.Count < MaxPoolSize) _packetPool.Add(p);
            }
        }

        if (_timerPeriodActive)
        {
            try { Native.TimeEndPeriod(1); } catch { }
            _timerPeriodActive = false;
        }

        _stopping = false;
        EventLogBridge.Info("WinDivert interception engine stopped (standby mode, 0% CPU).");
    }

    public void RefreshPortMap()
    {
        if (_disposed || _stopping) return;

        try
        {
            // Double-buffer: populate into scratch arrays so WorkerLoop never sees Array.Clear in-flight
            Native.PopulatePortOwnerArrays(_scratchTcpPortOwners, _scratchUdpPortOwners);

            var oldTcp = _tcpPortOwners;
            var oldUdp = _udpPortOwners;
            _tcpPortOwners = _scratchTcpPortOwners;
            _udpPortOwners = _scratchUdpPortOwners;
            _scratchTcpPortOwners = oldTcp;
            _scratchUdpPortOwners = oldUdp;

            var portMap = Native.GetAllPortOwners();
            if (portMap != null && portMap.Count > 0)
            {
                foreach (var (port, pid) in portMap)
                {
                    if (port > 0 && pid > 0)
                    {
                        _portToPid[port] = pid;
                        if (!_pidToAppPath.ContainsKey(pid))
                        {
                            string path = Native.ProcessImagePath((uint)pid);
                            _pidToAppPath[pid] = string.IsNullOrEmpty(path) ? "" : path;
                        }
                    }
                }
            }

            // Periodic cleanup of stale PIDs (> 60s without packets)
            DateTime now = DateTime.UtcNow;
            if ((now - _lastStaleCleanupUtc).TotalSeconds > 30)
            {
                _lastStaleCleanupUtc = now;
                foreach (var kvp in _pidLastSeen)
                {
                    if ((now - kvp.Value).TotalSeconds > 60)
                    {
                        _pidLastSeen.TryRemove(kvp.Key, out _);
                        _bytesIn.TryRemove(kvp.Key, out _);
                        _bytesOut.TryRemove(kvp.Key, out _);
                        // Retain _processIn/OutBuckets across idle periods (M3)
                        _pidToAppPath.TryRemove(kvp.Key, out _);
                    }
                }

                // Sweep unhandled held packets older than 25 seconds
                CleanupStaleHeldPackets(25.0);
            }
        }
        catch { }
    }

    public void UpdatePortMap(IEnumerable<(ushort Port, int Pid)> mappings)
    {
        var tcp = _tcpPortOwners;
        var udp = _udpPortOwners;
        foreach (var (port, pid) in mappings)
        {
            if (port > 0 && pid > 0)
            {
                _portToPid[port] = pid;
                tcp[port] = pid;
                udp[port] = pid;
            }
        }
    }

    public IReadOnlyCollection<int> GetActivePids()
    {
        var pids = new HashSet<int>(_bytesIn.Keys);
        pids.UnionWith(_bytesOut.Keys);
        return pids;
    }

    public bool TryGetStats(int pid, out ulong bytesIn, out ulong bytesOut)
    {
        Interlocked.Increment(ref _tryGetStatsCalls);
        bool hasIn = _bytesIn.TryGetValue(pid, out bytesIn);
        bool hasOut = _bytesOut.TryGetValue(pid, out bytesOut);
        if (hasIn || hasOut)
        {
            Interlocked.Increment(ref _tryGetStatsHits);
        }
        return hasIn || hasOut;
    }

    private int _reopenAttempts = 0;
    private void TryReopenWorker()
    {
        if (_stopping || _disposed || !NeedsPacketInterception) return;
        if (Interlocked.Increment(ref _reopenAttempts) <= 3)
        {
            try
            {
                _divert?.Dispose();
                _divert = new WinDivert("!loopback and (tcp or udp)", WinDivertLayer.Network, 0, WinDivertFlag.None)
                {
                    QueueLength = 16384,
                    QueueSize = 32 * 1024 * 1024,
                    QueueTime = TimeSpan.FromSeconds(2)
                };
                _available = true;
                UnavailableReason = "none";
                _workerThread = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Normal,
                    Name = "WinDivert-ActivePacketRouter"
                };
                _workerThread.Start();
                EventLogBridge.Info("WinDivert worker loop successfully reopened.");
            }
            catch (Exception ex)
            {
                _available = false;
                UnavailableReason = $"worker reopen failed: {ex.Message}";
                EventLogBridge.Warn($"WinDivert worker reopen failed: {ex.Message}");
            }
        }
        else
        {
            _available = false;
            UnavailableReason = "stopped";
        }
    }

    public bool TryGetTotalStats(out ulong totalIn, out ulong totalOut)
    {
        totalIn = Interlocked.Read(ref _totalBytesIn);
        totalOut = Interlocked.Read(ref _totalBytesOut);
        return _available;
    }

    #region Configuration APIs (Limits & Priorities)

    public int GlobalDownloadLimitKbps
    {
        get => _globalInBucket != null ? (int)(_globalInBucket.BytesPerSec * 8 / 1024) : 0;
        set => SetGlobalLimit(value, isDownload: true, enabled: value > 0);
    }

    public int GlobalUploadLimitKbps
    {
        get => _globalOutBucket != null ? (int)(_globalOutBucket.BytesPerSec * 8 / 1024) : 0;
        set => SetGlobalLimit(value, isDownload: false, enabled: value > 0);
    }

    public void SetGlobalLimit(int limitKbps, bool isDownload = true, bool enabled = true)
    {
        if (limitKbps <= 0 || !enabled)
        {
            if (isDownload) _globalInBucket = null;
            else _globalOutBucket = null;
            UpdateEngineState();
            return;
        }

        long bytesPerSec = (long)limitKbps * 1024 / 8;
        if (isDownload)
        {
            if (_globalInBucket == null) _globalInBucket = new TokenBucket(bytesPerSec);
            else _globalInBucket.SetRate(bytesPerSec);
        }
        else
        {
            if (_globalOutBucket == null) _globalOutBucket = new TokenBucket(bytesPerSec);
            else _globalOutBucket.SetRate(bytesPerSec);
        }
        UpdateEngineState();
    }

    public void ClearGlobalLimit()
    {
        _globalInBucket = null;
        _globalOutBucket = null;
        UpdateEngineState();
    }

    public void SetDownloadLimit(int pid, int limitKbps) => SetProcessLimit(pid, limitKbps, isDownload: true, enabled: limitKbps > 0);
    public void ClearDownloadLimit(int pid) => ClearProcessLimit(pid);
    public void SetUploadLimit(int pid, int limitKbps) => SetProcessLimit(pid, limitKbps, isDownload: false, enabled: limitKbps > 0);
    public void ClearUploadLimit(int pid) => SetProcessLimit(pid, 0, isDownload: false, enabled: false);

    public void SetProcessLimit(int pid, int limitKbps, bool isDownload = true, bool enabled = true)
    {
        if (limitKbps <= 0 || !enabled)
        {
            if (isDownload)
            {
                _processDlLimits.TryRemove(pid, out _);
                _processInBuckets.TryRemove(pid, out _);
            }
            else
            {
                _processUlLimits.TryRemove(pid, out _);
                _processOutBuckets.TryRemove(pid, out _);
            }
            UpdateEngineState();
            return;
        }

        long bytesPerSec = (long)limitKbps * 1024 / 8;
        if (isDownload)
        {
            _processDlLimits[pid] = limitKbps;
            _processInBuckets.AddOrUpdate(pid, _ => new TokenBucket(bytesPerSec), (_, b) => { b.SetRate(bytesPerSec); return b; });
        }
        else
        {
            _processUlLimits[pid] = limitKbps;
            _processOutBuckets.AddOrUpdate(pid, _ => new TokenBucket(bytesPerSec), (_, b) => { b.SetRate(bytesPerSec); return b; });
        }
        UpdateEngineState();
    }

    public void ClearProcessLimit(int pid)
    {
        _processDlLimits.TryRemove(pid, out _);
        _processUlLimits.TryRemove(pid, out _);
        _processInBuckets.TryRemove(pid, out _);
        _processOutBuckets.TryRemove(pid, out _);
        UpdateEngineState();
    }

    public void SetAppLimit(string exeOrPath, int limitKbps, bool isDownload = true, bool enabled = true)
    {
        string key = Path.GetFileName(exeOrPath).ToLowerInvariant();
        if (limitKbps <= 0 || !enabled)
        {
            if (isDownload) { _appDlLimits.TryRemove(key, out _); _appInBuckets.TryRemove(key, out _); }
            else { _appUlLimits.TryRemove(key, out _); _appOutBuckets.TryRemove(key, out _); }
            UpdateEngineState();
            return;
        }

        long bytesPerSec = (long)limitKbps * 1024 / 8;
        if (isDownload)
        {
            _appDlLimits[key] = limitKbps;
            _appInBuckets.AddOrUpdate(key, _ => new TokenBucket(bytesPerSec), (_, b) => { b.SetRate(bytesPerSec); return b; });
        }
        else
        {
            _appUlLimits[key] = limitKbps;
            _appOutBuckets.AddOrUpdate(key, _ => new TokenBucket(bytesPerSec), (_, b) => { b.SetRate(bytesPerSec); return b; });
        }
        UpdateEngineState();
    }

    public void ClearAppLimit(string exeOrPath)
    {
        string key = Path.GetFileName(exeOrPath).ToLowerInvariant();
        _appDlLimits.TryRemove(key, out _);
        _appUlLimits.TryRemove(key, out _);
        _appInBuckets.TryRemove(key, out _);
        _appOutBuckets.TryRemove(key, out _);
        UpdateEngineState();
    }

    public void ClearAllAppLimits()
    {
        _appDlLimits.Clear();
        _appUlLimits.Clear();
        _appInBuckets.Clear();
        _appOutBuckets.Clear();
        _appPriorities.Clear();
        UpdateEngineState();
    }

    public void SetConnectionLimit(string connectionKey, int limitKbps, bool isDownload = true, bool enabled = true)
    {
        if (limitKbps <= 0 || !enabled)
        {
            if (isDownload) { _connDlLimits.TryRemove(connectionKey, out _); _connInBuckets.TryRemove(connectionKey, out _); }
            else { _connUlLimits.TryRemove(connectionKey, out _); _connOutBuckets.TryRemove(connectionKey, out _); }
            UpdateEngineState();
            return;
        }

        long bytesPerSec = (long)limitKbps * 1024 / 8;
        if (isDownload)
        {
            _connDlLimits[connectionKey] = limitKbps;
            _connInBuckets.AddOrUpdate(connectionKey, _ => new TokenBucket(bytesPerSec), (_, b) => { b.SetRate(bytesPerSec); return b; });
        }
        else
        {
            _connUlLimits[connectionKey] = limitKbps;
            _connOutBuckets.AddOrUpdate(connectionKey, _ => new TokenBucket(bytesPerSec), (_, b) => { b.SetRate(bytesPerSec); return b; });
        }
        UpdateEngineState();
    }

    public void ClearConnectionLimit(string connectionKey)
    {
        _connDlLimits.TryRemove(connectionKey, out _);
        _connUlLimits.TryRemove(connectionKey, out _);
        _connInBuckets.TryRemove(connectionKey, out _);
        _connOutBuckets.TryRemove(connectionKey, out _);
        UpdateEngineState();
    }

    public void SetFilterLimit(string filterName, int limitKbps, bool isDownload = true, bool enabled = true)
    {
        if (limitKbps <= 0 || !enabled)
        {
            if (isDownload) { _filterDlLimits.TryRemove(filterName, out _); _filterInBuckets.TryRemove(filterName, out _); }
            else { _filterUlLimits.TryRemove(filterName, out _); _filterOutBuckets.TryRemove(filterName, out _); }
            UpdateEngineState();
            return;
        }

        long bytesPerSec = (long)limitKbps * 1024 / 8;
        if (isDownload)
        {
            _filterDlLimits[filterName] = limitKbps;
            _filterInBuckets.AddOrUpdate(filterName, _ => new TokenBucket(bytesPerSec), (_, b) => { b.SetRate(bytesPerSec); return b; });
        }
        else
        {
            _filterUlLimits[filterName] = limitKbps;
            _filterOutBuckets.AddOrUpdate(filterName, _ => new TokenBucket(bytesPerSec), (_, b) => { b.SetRate(bytesPerSec); return b; });
        }
        UpdateEngineState();
    }

    public void ClearFilterLimit(string filterName)
    {
        _filterDlLimits.TryRemove(filterName, out _);
        _filterUlLimits.TryRemove(filterName, out _);
        _filterInBuckets.TryRemove(filterName, out _);
        _filterOutBuckets.TryRemove(filterName, out _);
        UpdateEngineState();
    }

    public void SetProcessPriority(int pid, TrafficPriority priority)
    {
        if (priority == TrafficPriority.Normal) _processPriorities.TryRemove(pid, out _);
        else _processPriorities[pid] = priority;
        UpdateEngineState();
    }

    public void SetAppPriority(string exeOrPath, TrafficPriority priority)
    {
        string k = Path.GetFileName(exeOrPath).ToLowerInvariant();
        if (priority == TrafficPriority.Normal) _appPriorities.TryRemove(k, out _);
        else _appPriorities[k] = priority;
        UpdateEngineState();
    }

    public void SetConnectionPriority(string connectionKey, TrafficPriority priority)
    {
        if (priority == TrafficPriority.Normal) _connPriorities.TryRemove(connectionKey, out _);
        else _connPriorities[connectionKey] = priority;
        UpdateEngineState();
    }

    public void SetFilterPriority(string filterName, TrafficPriority priority)
    {
        if (priority == TrafficPriority.Normal) _filterPriorities.TryRemove(filterName, out _);
        else _filterPriorities[filterName] = priority;
        UpdateEngineState();
    }

    public void SetLineSpeed(long downloadBps, long uploadBps)
    {
        _downloadLineSpeedBps = Math.Max(10240, downloadBps);
        _uploadLineSpeedBps = Math.Max(10240, uploadBps);
    }

    public void SetPriorityReservations(double criticalPct, double highPct, double normalPct, double lowPct)
    {
        double sum = criticalPct + highPct + normalPct + lowPct;
        if (sum <= 0) return;
        _criticalReservationPct = (criticalPct / sum) * 100.0;
        _highReservationPct = (highPct / sum) * 100.0;
        _normalReservationPct = (normalPct / sum) * 100.0;
        _lowReservationPct = (lowPct / sum) * 100.0;
    }

    public (long InBps, long OutBps) GetDetectedLineSpeed()
    {
        return ((long)_peakObservedInRate, (long)_peakObservedOutRate);
    }

    #endregion

    #region Packet Processing & Pacing Engine

    private void WorkerLoop()
    {
        var divert = _divert;
        if (divert == null) return;

        try
        {
            int consecutiveRecvFailures = 0;
            using var packet = new WinDivertPacket(65535);
            var addr = new WinDivertAddress();

            while (!_stopping && !_disposed)
            {
                int recvLen;
                try
                {
                    if (_divert == null) break; recvLen = divert.Recv(packet, addr);
                    consecutiveRecvFailures = 0;
                    LastPacketUtc = DateTime.UtcNow;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_stopping || _disposed) break;
                    consecutiveRecvFailures++;
                    if (consecutiveRecvFailures > 10)
                    {
                        EventLogBridge.Warn($"WinDivert Recv failed repeatedly ({consecutiveRecvFailures}): {ex.Message}");
                        Thread.Sleep(50);
                    }
                    if (consecutiveRecvFailures > 100)
                    {
                        _available = false;
                        UnavailableReason = $"recv error loop: {ex.Message}";
                        break;
                    }
                    continue;
                }

                if (recvLen <= 0) continue;
                Interlocked.Increment(ref _totalPacketsReceived);

                bool isOutbound = (addr.Flags & WinDivertAddressFlag.Outbound) != 0;
                if (isOutbound)
                {
                    _reopenAttempts = 0; Interlocked.Add(ref _totalBytesOut, (ulong)recvLen);
                    if (recvLen * 10 > _peakObservedOutRate) _peakObservedOutRate = recvLen * 10;
                }
                else
                {
                    Interlocked.Add(ref _totalBytesIn, (ulong)recvLen);
                    if (recvLen * 10 > _peakObservedInRate) _peakObservedInRate = recvLen * 10;
                }

                ushort srcPort = 0;
                ushort dstPort = 0;
                byte protocol = 0;
                bool isIpv6 = false;

                ReadOnlySpan<byte> span = packet.Span;
                if (span.Length >= 20)
                {
                    byte ver = (byte)(span[0] >> 4);
                    if (ver == 4)
                    {
                        protocol = span[9];
                        byte ihl = (byte)((span[0] & 0x0F) * 4);
                        if ((protocol == 6 || protocol == 17) && span.Length >= ihl + 4)
                        {
                            srcPort = (ushort)((span[ihl] << 8) | span[ihl + 1]);
                            dstPort = (ushort)((span[ihl + 2] << 8) | span[ihl + 3]);
                        }
                    }
                    else if (ver == 6 && span.Length >= 40)
                    {
                        isIpv6 = true;
                        protocol = span[6];
                        if ((protocol == 6 || protocol == 17) && span.Length >= 44)
                        {
                            srcPort = (ushort)((span[40] << 8) | span[41]);
                            dstPort = (ushort)((span[42] << 8) | span[43]);
                        }
                    }
                }

                if (srcPort == 0 && dstPort == 0)
                {
                    divert.Send(packet, addr);
                    continue;
                }

                try
                {
                    ushort localPort = isOutbound ? srcPort : dstPort;
                    ushort remotePort = isOutbound ? dstPort : srcPort;

                    // 1. FAST BYPASS: DNS traffic (UDP/TCP port 53) must NEVER be delayed or blocked accidentally!
                    if (localPort == 53 || remotePort == 53)
                    {
                        divert.Send(packet, addr);
                        continue;
                    }

                    // 2. FAST BYPASS: Loopback check directly from raw bytes (zero allocations)
                    if (!isIpv6)
                    {
                        if (span[12] == 127 || span[16] == 127)
                        {
                            divert.Send(packet, addr);
                            continue;
                        }
                    }
                    else
                    {
                        if (span[23] == 1 || span[39] == 1)
                        {
                            divert.Send(packet, addr);
                            continue;
                        }
                    }

                    // 3. FAST BYPASS: Outbound TCP Pure ACKs (length <= 64, protocol TCP) must not be throttled on upload
                    if (protocol == 6 && recvLen <= 64 && isOutbound)
                    {
                        divert.Send(packet, addr);
                        continue;
                    }

                    // O(1) lock-free port to PID lookup
                    int pid = 0;
                    if (localPort > 0)
                    {
                        var tcpArr = _tcpPortOwners;
                        var udpArr = _udpPortOwners;
                        pid = protocol == 6 ? Volatile.Read(ref tcpArr[localPort]) : Volatile.Read(ref udpArr[localPort]);
                        if (pid <= 0)
                        {
                            _portToPid.TryGetValue(localPort, out pid);
                        }
                    }
                    if (pid <= 0)
                    {
                        Interlocked.Increment(ref _totalPacketsPidZero);
                    }
                    else
                    {
                        _pidLastSeen[pid] = DateTime.UtcNow;
                    }

                    // Update per-PID telemetry accumulators
                    if (pid > 0)
                    {
                        if (isOutbound)
                        {
                            if (pid < 65536) Interlocked.Add(ref _fastBytesOut[pid], (ulong)recvLen);
                            _bytesOut.AddOrUpdate(pid, AddLenFactory, UpdateLenFactory, (ulong)recvLen);
                        }
                        else
                        {
                            if (pid < 65536) Interlocked.Add(ref _fastBytesIn[pid], (ulong)recvLen);
                            _bytesIn.AddOrUpdate(pid, AddLenFactory, UpdateLenFactory, (ulong)recvLen);
                        }
                    }

                    // In Sniff mode: Windows kernel already forwarded the packet at native line speed.
                    // DO NOT call divert.Send()!
                    if (_isSniffMode)
                    {
                        continue;
                    }

                    // FAST BYPASS 1: In active diversion mode, forward immediately if stream is unthrottled
                    if (!IsBlockerEnabled && !IsPrioritiesEnabled && !HasActiveLimits && !HasActiveUnifiedRules)
                    {
                        divert.Send(packet, addr);
                        continue;
                    }

                    // FAST BYPASS 2: Per-process / per-app bypass when this specific stream has no active limits
                    if (!IsBlockerEnabled && !IsPrioritiesEnabled && !HasActiveUnifiedRules &&
                        _globalInBucket == null && _globalOutBucket == null &&
                        _connInBuckets.IsEmpty && _connOutBuckets.IsEmpty)
                    {
                        bool hasPidLimit = pid > 0 && (isOutbound ? _processOutBuckets.ContainsKey(pid) : _processInBuckets.ContainsKey(pid));
                        if (!hasPidLimit)
                        {
                            string? app = null;
                            if (pid > 0 && _pidToAppPath.TryGetValue(pid, out var p)) app = p;
                            bool hasAppLimit = false;
                            if (!string.IsNullOrEmpty(app))
                            {
                                var appBuckets = isOutbound ? _appOutBuckets : _appInBuckets;
                                if (appBuckets.ContainsKey(app) ||
                                    appBuckets.ContainsKey(Path.GetFileName(app)) ||
                                    appBuckets.ContainsKey(Path.GetFileNameWithoutExtension(app)))
                                {
                                    hasAppLimit = true;
                                }
                            }
                            if (!hasAppLimit)
                            {
                                if (pid > 0)
                                {
                                    if (isOutbound) _bytesOut.AddOrUpdate(pid, AddLenFactory, UpdateLenFactory, (ulong)recvLen);
                                    else _bytesIn.AddOrUpdate(pid, AddLenFactory, UpdateLenFactory, (ulong)recvLen);
                                }
                                divert.Send(packet, addr);
                                continue;
                            }
                        }
                    }

                    // Flow rule decision cache: avoids string formatting, IP allocations and rule evaluation on every packet
                    ulong flowId = ((ulong)protocol << 32) | ((ulong)localPort << 16) | (ulong)remotePort;
                    RuleEvaluationResult eval;
                    string appName = "";
                    string appPath = "";
                    string localIpStr = "";
                    string remoteIpStr = "";
                    bool isLocal = false;
                    long nowTicks = Stopwatch.GetTimestamp();

                    if (_flowCache.TryGetValue(flowId, out var cachedEntry) &&
                        (nowTicks - cachedEntry.Timestamp) < (Stopwatch.Frequency * 3))
                    {
                        eval = cachedEntry.Result;
                        appName = cachedEntry.AppName;
                        appPath = cachedEntry.AppPath;
                        localIpStr = cachedEntry.LocalIp;
                        remoteIpStr = cachedEntry.RemoteIp;
                        isLocal = cachedEntry.IsLocal;
                    }
                    else
                    {
                        IPAddress? srcIp = !isIpv6 ? new IPAddress(span.Slice(12, 4)) : new IPAddress(span.Slice(8, 16));
                        IPAddress? dstIp = !isIpv6 ? new IPAddress(span.Slice(16, 4)) : new IPAddress(span.Slice(24, 16));
                        IPAddress? localIp = isOutbound ? srcIp : dstIp;
                        IPAddress? remoteIp = isOutbound ? dstIp : srcIp;
                        remoteIpStr = remoteIp?.ToString() ?? "";
                        localIpStr = localIp?.ToString() ?? "";
                        if (pid > 0 && _pidToAppPath.TryGetValue(pid, out var cachedPath))
                        {
                            appPath = cachedPath;
                        }
                        appName = string.IsNullOrEmpty(appPath) ? "" : Path.GetFileName(appPath);
                        string domain = DnsResolver?.Lookup(remoteIpStr) ?? "";
                        isLocal = IpRangeMatcher.IsPrivate(remoteIpStr) || IpRangeMatcher.IsLoopback(remoteIpStr);

                        var ctx = new ConnectionContext(appName, appPath, localIpStr, localPort, remoteIpStr, remotePort,
                            protocol == 6 ? "TCP" : (protocol == 17 ? "UDP" : protocol.ToString()), domain, "", isLocal)
                        {
                            Pid = pid,
                            IsInbound = !isOutbound
                        };

                        eval = RuleEvaluator.Evaluate(_unifiedRules, ctx, _filters);

                        if (_flowCache.Count > 10000) _flowCache.Clear();
                        _flowCache[flowId] = new FlowCacheEntry(eval, appName, appPath, localIpStr, remoteIpStr, isLocal, nowTicks);
                    }

                    // PRECEDENCE TIER 1: IGNORE RULES (Item 4.03)
                    if (eval.IsBypassed && !eval.CountInStats)
                    {
                        divert.Send(packet, addr);
                        continue;
                    }

                    // Record process telemetry (only when not bypassed by IgnoreData)
                    if (pid > 0)
                    {
                        if (isOutbound) _bytesOut.AddOrUpdate(pid, AddLenFactory, UpdateLenFactory, (ulong)recvLen);
                        else _bytesIn.AddOrUpdate(pid, AddLenFactory, UpdateLenFactory, (ulong)recvLen);
                    }

                    // FAST BYPASS: If no blocking, no priorities, and NO active limits exist, send immediately
                    if (!IsBlockerEnabled && !IsPrioritiesEnabled && !HasActiveLimits && !HasActiveUnifiedRules)
                    {
                        divert.Send(packet, addr);
                        continue;
                    }

                    // PRECEDENCE TIER 2: BLOCKER RULES (Item 4.04, 3.02, 3.03, 3.04)
                    if (IsBlockerEnabled)
                    {
                        if (eval.BlockAction == BlockerAction.Deny)
                        {
                            // Packet dropped at wire speed
                            continue;
                        }

                        // Check if packet is essential infrastructure traffic that must NEVER be held or blocked by default "Ask"
                        bool isEssential = isLocal ||
                                           localPort == 53 || remotePort == 53 ||
                                           localPort == 67 || localPort == 68 || remotePort == 67 || remotePort == 68 ||
                                           pid <= 4 || string.IsNullOrEmpty(remoteIpStr) ||
                                           remoteIpStr.StartsWith("127.") || remoteIpStr == "::1";

                        bool isAsk = (eval.BlockAction == BlockerAction.Ask ||
                                     (eval.BlockAction == BlockerAction.NotSet && string.Equals(_defaultBlockerAction, "Ask", StringComparison.OrdinalIgnoreCase)))
                                     && !isEssential;

                        if (isAsk)
                        {
                            string flowKey = $"pid:{pid}|{localIpStr}:{localPort}|{remoteIpStr}:{remotePort}";
                            string appKey = !string.IsNullOrEmpty(appName) ? appName.ToLowerInvariant() : (pid > 0 ? $"pid_{pid}" : remoteIpStr);

                            if (_heldDecisions.TryGetValue(flowKey, out var dec) || _heldDecisions.TryGetValue(appKey, out dec))
                            {
                                if (dec == BlockerAction.Deny)
                                {
                                    continue; // Dropped according to user's decision
                                }
                                // If allowed, fall through to traffic accounting & rate limits!
                            }
                            else
                            {
                                ulong id = (ulong)Interlocked.Increment(ref _nextHeldPacketId);
                                byte[] copy = packet.Span.Slice(0, recvLen).ToArray();
                                var held = new HeldPacket
                                {
                                    Id = id,
                                    Data = copy,
                                    Address = addr,
                                    CreatedAtUtc = DateTime.UtcNow,
                                    Pid = pid,
                                    ProcessName = appName,
                                    AppPath = appPath,
                                    LocalIp = localIpStr,
                                    LocalPort = localPort,
                                    RemoteIp = remoteIpStr,
                                    RemotePort = remotePort,
                                    Protocol = protocol == 6 ? "TCP" : (protocol == 17 ? "UDP" : protocol.ToString()),
                                    IsInbound = !isOutbound,
                                    FlowKey = flowKey
                                };

                                _heldPackets[id] = held;
                                OnPacketHeld?.Invoke(held);
                                continue; // DO NOT send - packet is held in user space buffer awaiting decision
                            }
                        }
                    }

                    // PRECEDENCE TIER 3: QUOTA ENFORCEMENT
                    if (eval.QuotaExceeded && string.Equals(eval.QuotaAction, "Block", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // Dropped on quota exhaustion
                    }
                    if (eval.MatchedRules.Count > 0)
                    {
                        foreach (var mr in eval.MatchedRules)
                        {
                            if (mr.Type == RuleType.Quota)
                            {
                                mr.AddUsage(isOutbound ? 0 : recvLen, isOutbound ? recvLen : 0);
                            }
                        }
                    }

                    // PRECEDENCE TIER 4: IGNORE-LIMIT RULES (Item 4.03)
                    if (eval.IsBypassed)
                    {
                        divert.Send(packet, addr);
                        continue;
                    }

                    // FAST-PATH WIRE-SPEED BYPASS CHECK (< 5 µs latency overhead)
                    // If no limits (neither global nor individual) and no priorities are active:
                    if (!IsLimiterEnabled && !HasActiveLimits && !HasActiveUnifiedRules && !IsPrioritiesEnabled)
                    {
                        divert.Send(packet, addr);
                        continue;
                    }

                    // Construct 5-tuple connection key only if connection limits/priorities exist
                    string connKey = (!_connPriorities.IsEmpty || !_connInBuckets.IsEmpty || !_connOutBuckets.IsEmpty)
                        ? $"{(isIpv6 ? "6" : "4")}|{localPort}|{remotePort}"
                        : "";

                    // Evaluate Rate Limits & Priority
                    double requiredDelay = 0;

                    // 1. Evaluate Priority
                    TrafficPriority priority = TrafficPriority.Normal;
                    if (IsPrioritiesEnabled)
                    {
                        if (eval.Priority != TrafficPriority.Normal) priority = eval.Priority;
                        else if (!string.IsNullOrEmpty(connKey) && _connPriorities.TryGetValue(connKey, out var cp)) priority = cp;
                        else if (pid > 0 && _processPriorities.TryGetValue(pid, out var pp)) priority = pp;

                        if (isOutbound)
                        {
                            if (priority == TrafficPriority.Critical) _smoothedCriticalOutRate = 0.9 * _smoothedCriticalOutRate + 0.1 * recvLen * 10;
                            else if (priority == TrafficPriority.High) _smoothedHighOutRate = 0.9 * _smoothedHighOutRate + 0.1 * recvLen * 10;
                        }
                        else
                        {
                            if (priority == TrafficPriority.Critical) _smoothedCriticalInRate = 0.9 * _smoothedCriticalInRate + 0.1 * recvLen * 10;
                            else if (priority == TrafficPriority.High) _smoothedHighInRate = 0.9 * _smoothedHighInRate + 0.1 * recvLen * 10;
                        }
                    }

                    // 2. Evaluate Hierarchical Token Buckets (Global and Individual limits)
                    if (IsLimiterEnabled || HasActiveLimits || eval.EffectiveLimitKbps > 0)
                    {
                        requiredDelay = CalculatePacingDelay(recvLen, isOutbound, pid, connKey, appName, appPath, eval.EffectiveLimitKbps);
                    }

                    // 3. Priority QoS Scheduling Delay
                    if (IsPrioritiesEnabled)
                    {
                        double priorityDelay = CalculatePriorityDelay(recvLen, isOutbound, priority);
                        if (priorityDelay > requiredDelay)
                        {
                            requiredDelay = priorityDelay;
                        }
                    }

                    // FAST PATH: If delay is 0, send immediately without queuing!
                    if (requiredDelay <= 0.00005) // < 50 microseconds
                    {
                        divert.Send(packet, addr);
                        continue;
                    }

                    // Drop packet if delay exceeds bufferbloat threshold (200ms)
                    if (requiredDelay > MaxQueueDelaySeconds)
                    {
                        // Tail drop: packet discarded, TCP congestion window backs off
                        continue;
                    }

                    // Queue packet for sub-millisecond pacing
                    EnqueuePacedPacket(packet, ref addr, recvLen, requiredDelay, priority);
                }
                catch
                {
                    // Fail-safe: Always re-inject packet if an unexpected error occurs during packet processing
                    try { _divert?.Send(packet, addr); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _available = false;
            EventLogBridge.Warn($"WinDivert worker loop terminated: {ex.Message}");
            TryReopenWorker();
        }
    }

    private double CalculatePacingDelay(int packetLength, bool isOutbound, int pid, string connKey, string appName, string appPath, int effectiveRuleLimitKbps = 0)
    {
        double maxDelay = 0;

        // Level 0: Global whole-machine limit (Item 1.05) - strictly guarded by IsLimiterEnabled master toggle
        if (IsLimiterEnabled)
        {
            TokenBucket? globalBucket = isOutbound ? _globalOutBucket : _globalInBucket;
            if (globalBucket != null)
            {
                if (!globalBucket.TryConsume(packetLength, out double d) && d > maxDelay)
                    maxDelay = d;
            }
        }

        // Level 0.5: Unified Rule Limit (Item 4.01, 4.04)
        if (effectiveRuleLimitKbps > 0)
        {
            long bytesPerSec = (long)effectiveRuleLimitKbps * 1024 / 8;
            double waitSec = (double)packetLength / bytesPerSec;
            if (waitSec > maxDelay) maxDelay = waitSec;
        }

        // Level 1: Application limit (by executable name, extension variants, or full path)
        var appBuckets = isOutbound ? _appOutBuckets : _appInBuckets;
        TokenBucket? aBucket = null;
        if (!appBuckets.IsEmpty && !string.IsNullOrEmpty(appName))
        {
            if (appBuckets.TryGetValue(appName, out aBucket))
            {
                if (!aBucket.TryConsume(packetLength, out double d) && d > maxDelay)
                    maxDelay = d;
            }
            else if (appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                     appBuckets.TryGetValue(Path.GetFileNameWithoutExtension(appName), out aBucket))
            {
                if (!aBucket.TryConsume(packetLength, out double d) && d > maxDelay)
                    maxDelay = d;
            }
            else if (!appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                     appBuckets.TryGetValue(appName + ".exe", out aBucket))
            {
                if (!aBucket.TryConsume(packetLength, out double d) && d > maxDelay)
                    maxDelay = d;
            }
        }
        if (aBucket == null && !string.IsNullOrEmpty(appPath))
        {
            if (appBuckets.TryGetValue(appPath, out aBucket))
            {
                if (!aBucket.TryConsume(packetLength, out double d) && d > maxDelay)
                    maxDelay = d;
            }
        }

        // Level 2: Connection limit (Item 1.03)
        if (!string.IsNullOrEmpty(connKey))
        {
            var connBuckets = isOutbound ? _connOutBuckets : _connInBuckets;
            if (connBuckets.TryGetValue(connKey, out var cBucket))
            {
                if (!cBucket.TryConsume(packetLength, out double d) && d > maxDelay)
                    maxDelay = d;
            }
        }

        // Level 3: Process limit (PID) (Item 1.12) - only evaluate if no app bucket already applied
        if (pid > 0 && aBucket == null)
        {
            var procBuckets = isOutbound ? _processOutBuckets : _processInBuckets;
            if (procBuckets.TryGetValue(pid, out var pBucket))
            {
                if (!pBucket.TryConsume(packetLength, out double d) && d > maxDelay)
                    maxDelay = d;
            }
        }

        return maxDelay;
    }

    private double CalculatePriorityDelay(int packetLength, bool isOutbound, TrafficPriority priority)
    {
        // Critical and Normal traffic have 0 pacing delay when no limit contends (no 3ms penalty)
        if (priority == TrafficPriority.Critical || priority == TrafficPriority.Normal)
        {
            return 0; // Immediate release
        }

        // Under Reversed Limits mode (Item 2.11), lower priorities are throttled based on higher usage
        if (UseReversedLimits)
        {
            long lineSpeed = isOutbound ? _uploadLineSpeedBps : _downloadLineSpeedBps;
            double critUsage = isOutbound ? _smoothedCriticalOutRate : _smoothedCriticalInRate;
            double highUsage = isOutbound ? _smoothedHighOutRate : _smoothedHighInRate;

            long availableForLower = (long)Math.Max(10240, lineSpeed - critUsage - highUsage);
            if (priority == TrafficPriority.Low)
            {
                // Low gets throttled proportionally to remaining bandwidth
                double lowCeiling = availableForLower * (_lowReservationPct / (_normalReservationPct + _lowReservationPct));
                if (lowCeiling > 0)
                {
                    double waitSec = (double)packetLength / lowCeiling;
                    return Math.Min(0.100, waitSec);
                }
            }
        }

        // Weighted pacing delay for High and Low
        return priority switch
        {
            TrafficPriority.High => 0.001,   // 1 ms smooth pacing
            TrafficPriority.Low => 0.008,    // 8 ms background pacing
            _ => 0
        };
    }

    private void EnqueuePacedPacket(WinDivertPacket packet, ref WinDivertAddress addr, int length, double delaySeconds, TrafficPriority priority)
    {
        if (_stopping || _disposed) return;

        if (!_packetPool.TryTake(out var paced))
        {
            paced = new PacedPacket();
        }

        packet.Span.Slice(0, length).CopyTo(paced.Packet.Span);
        paced.Length = length;
        paced.Address = addr;
        paced.Priority = priority;

        long delayTicks = (long)(delaySeconds * Stopwatch.Frequency);
        paced.ReleaseTicks = Stopwatch.GetTimestamp() + delayTicks;

        lock (_pacingLock)
        {
            if (_pacingQueue.Count > 16384)
            {
                // Queue overflow: recycle immediately and tail drop
                _packetPool.Add(paced);
                return;
            }
            _pacingQueue.Enqueue(paced, paced.ReleaseTicks);
            _pacingSignal.Set();
        }
    }

    private void PacerLoop()
    {
        while (!_stopping && !_disposed)
        {
            PacedPacket? packetToRelease = null;
            long nextReleaseTicks = 0;

            lock (_pacingLock)
            {
                if (_pacingQueue.Count > 0)
                {
                    packetToRelease = _pacingQueue.Peek();
                    nextReleaseTicks = packetToRelease.ReleaseTicks;
                }
                else
                {
                    _pacingSignal.Reset();
                }
            }

            if (packetToRelease == null)
            {
                _pacingSignal.Wait(20);
                continue;
            }

            long now = Stopwatch.GetTimestamp();
            long diffTicks = nextReleaseTicks - now;

            if (diffTicks > 0)
            {
                double remainingMs = (double)diffTicks * 1000.0 / Stopwatch.Frequency;
                if (remainingMs > 2.0)
                {
                    // Tier 1: OS wait (0% CPU)
                    _pacingSignal.Reset();
                    _pacingSignal.Wait(Math.Clamp((int)remainingMs, 1, 10));
                    continue;
                }
                else if (remainingMs > 0.3)
                {
                    // Tier 2: Yield CPU slice
                    _pacingSignal.Reset();
                    _pacingSignal.Wait(1);
                    continue;
                }
                else
                {
                    // Tier 3: Micro-wait for sub-millisecond precision
                    Thread.SpinWait(20);
                    continue;
                }
            }

            // Time reached: Dequeue and transmit ready packets in batch
            List<PacedPacket>? batch = null;
            lock (_pacingLock)
            {
                now = Stopwatch.GetTimestamp();
                while (_pacingQueue.Count > 0 && _pacingQueue.Peek().ReleaseTicks <= now)
                {
                    batch ??= new List<PacedPacket>(16);
                    batch.Add(_pacingQueue.Dequeue());
                    if (batch.Count >= 64) break;
                }
            }

            if (batch != null)
            {
                foreach (var p in batch)
                {
                    try
                    {
                        p.Packet.Length = p.Length;
                        _divert?.Send(p.Packet, p.Address);
                    }
                    catch { }

                    if (_packetPool.Count < MaxPoolSize)
                    {
                        _packetPool.Add(p);
                    }
                }
            }
        }
    }

    #endregion

    #region Connection Termination Tools (Section 24)

    /// <summary>
    /// Kills a single TCP connection by key (Item 24.01).
    /// Uses Native.SetTcpEntry (TCB 12) for IPv4 and synthetic dual-ended TCP RST injection for IPv6.
    /// </summary>
    public bool KillConnection(string connectionKey)
    {
        if (string.IsNullOrWhiteSpace(connectionKey)) return false;

        // Try Native IPv4 SetTcpEntry first
        if (connectionKey.StartsWith("4|"))
        {
            if (Collector.KillConnection(connectionKey))
            {
                return true;
            }
        }

        // IPv6 or stubborn connection fallback: parse endpoints and inject TCP RST
        try
        {
            string[] parts = connectionKey.Split('|');
            if (parts.Length >= 3)
            {
                string[] localParts = parts[1].Split(':');
                string[] remoteParts = parts[2].Split(':');
                if (IPAddress.TryParse(localParts[0], out var localIp) &&
                    ushort.TryParse(localParts[1], out var localPort) &&
                    IPAddress.TryParse(remoteParts[0], out var remoteIp) &&
                    ushort.TryParse(remoteParts[1], out var remotePort))
                {
                    InjectTcpReset(localIp, localPort, remoteIp, remotePort, 0, 0, localIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    public int KillProcessConnections(int pid)
    {
        if (pid <= 0) return 0;
        return Collector.KillConnections(pid);
    }

    public int KillAppConnections(string exeOrPath, IEnumerable<int>? candidatePids = null)
    {
        string targetName = Path.GetFileName(exeOrPath).ToLowerInvariant();
        int totalKilled = 0;

        if (candidatePids != null)
        {
            foreach (int pid in candidatePids)
            {
                totalKilled += KillProcessConnections(pid);
            }
            return totalKilled;
        }

        // Enumerate running processes
        try
        {
            var processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    if (p.ProcessName.ToLowerInvariant().Equals(Path.GetFileNameWithoutExtension(targetName), StringComparison.OrdinalIgnoreCase))
                    {
                        totalKilled += KillProcessConnections(p.Id);
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }

        return totalKilled;
    }

    /// <summary>
    /// Kills a TCP connection by injecting RST packets based on IP/port endpoints (Section 24).
    /// </summary>
    public void KillConnection(string localIpStr, int localPort, string remoteIpStr, int remotePort)
    {
        if (IPAddress.TryParse(localIpStr, out var localIp) && IPAddress.TryParse(remoteIpStr, out var remoteIp))
        {
            bool isV6 = localIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            InjectTcpReset(localIp, (ushort)localPort, remoteIp, (ushort)remotePort, 0, 0, isV6);
        }
    }

    /// <summary>
    /// Injects dual-ended TCP RST packets (Local to Remote and Remote to Local) to instantaneously sever a TCP flow.
    /// </summary>
    public void InjectTcpReset(IPAddress localIp, ushort localPort, IPAddress remoteIp, ushort remotePort, uint seq, uint ack, bool isIpv6)
    {
        if (_divert == null || !_available) return;
        if (isIpv6) return; // IPv6 RST injection is not supported; connection teardown falls back safely to SetTcpEntry (W4)

        try
        {
            using var rstPacket = new WinDivertPacket(isIpv6 ? 60 : 40);
            var addr = new WinDivertAddress { Layer = WinDivertLayer.Network };

            // 1. Build and send Outbound RST (Local -> Remote)
            BuildTcpRstPacket(rstPacket.Span, localIp, remoteIp, localPort, remotePort, seq, isIpv6);
            addr.Flags = WinDivertAddressFlag.Outbound;
            _divert.Send(rstPacket, addr);

            // 2. Build and send Inbound RST (Remote -> Local) to close local socket
            BuildTcpRstPacket(rstPacket.Span, remoteIp, localIp, remotePort, localPort, ack, isIpv6);
            addr.Flags = WinDivertAddressFlag.None;
            _divert.Send(rstPacket, addr);
        }
        catch { }
    }

    private static void BuildTcpRstPacket(Span<byte> buffer, IPAddress srcIp, IPAddress dstIp, ushort srcPort, ushort dstPort, uint seq, bool isIpv6)
    {
        buffer.Clear();
        if (!isIpv6)
        {
            // IPv4 Header (20 bytes)
            buffer[0] = 0x45; // Version 4, IHL 5
            buffer[1] = 0x00; // DSCP / ECN
            buffer[2] = 0x00; buffer[3] = 40; // Total Length = 40
            buffer[8] = 128;  // TTL = 128
            buffer[9] = 6;    // Protocol = TCP
            srcIp.GetAddressBytes().CopyTo(buffer.Slice(12, 4));
            dstIp.GetAddressBytes().CopyTo(buffer.Slice(16, 4));

            // IPv4 Checksum
            ushort ipCksum = ComputeChecksum(buffer.Slice(0, 20));
            buffer[10] = (byte)(ipCksum >> 8);
            buffer[11] = (byte)(ipCksum & 0xFF);

            // TCP Header (20 bytes at offset 20)
            buffer[20] = (byte)(srcPort >> 8); buffer[21] = (byte)(srcPort & 0xFF);
            buffer[22] = (byte)(dstPort >> 8); buffer[23] = (byte)(dstPort & 0xFF);
            buffer[24] = (byte)(seq >> 24); buffer[25] = (byte)((seq >> 16) & 0xFF);
            buffer[26] = (byte)((seq >> 8) & 0xFF); buffer[27] = (byte)(seq & 0xFF);
            buffer[32] = 0x50; // Data offset 5 (20 bytes)
            buffer[33] = 0x04; // RST flag (0x04)
            buffer[34] = 0; buffer[35] = 0; // Window 0

            // TCP Checksum with pseudo-header
            ushort tcpCksum = ComputeTcpChecksum(buffer.Slice(20, 20), srcIp, dstIp);
            buffer[36] = (byte)(tcpCksum >> 8);
            buffer[37] = (byte)(tcpCksum & 0xFF);
        }
    }

    private static ushort ComputeChecksum(ReadOnlySpan<byte> span)
    {
        uint sum = 0;
        for (int i = 0; i < span.Length - 1; i += 2)
        {
            sum += (uint)((span[i] << 8) | span[i + 1]);
        }
        if ((span.Length & 1) != 0)
        {
            sum += (uint)(span[^1] << 8);
        }
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (ushort)~sum;
    }

    private static ushort ComputeTcpChecksum(ReadOnlySpan<byte> tcpSpan, IPAddress src, IPAddress dst)
    {
        uint sum = 0;
        byte[] srcBytes = src.GetAddressBytes();
        byte[] dstBytes = dst.GetAddressBytes();

        for (int i = 0; i < srcBytes.Length; i += 2)
            sum += (uint)((srcBytes[i] << 8) | srcBytes[i + 1]);
        for (int i = 0; i < dstBytes.Length; i += 2)
            sum += (uint)((dstBytes[i] << 8) | dstBytes[i + 1]);

        sum += 6; // Protocol TCP
        sum += (uint)tcpSpan.Length;

        for (int i = 0; i < tcpSpan.Length - 1; i += 2)
            sum += (uint)((tcpSpan[i] << 8) | tcpSpan[i + 1]);

        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);

        return (ushort)~sum;
    }

    #endregion

    #region Blocker Packet Holding (Items 3.02, 3.03, 3.04)

    public void ReleaseHeldPacket(ulong packetId, bool allow, bool rememberFlow = true)
    {
        if (_heldPackets.TryRemove(packetId, out var held))
        {
            if (rememberFlow && !string.IsNullOrEmpty(held.FlowKey))
            {
                if (_heldDecisions.Count > 2000) _heldDecisions.Clear(); _heldDecisions[held.FlowKey] = allow ? BlockerAction.Allow : BlockerAction.Deny;
            }

            if (allow && _divert != null && !_disposed)
            {
                try
                {
                    using var p = new WinDivertPacket(held.Data.Length);
                    held.Data.CopyTo(p.Span);
                    _divert.Send(p, held.Address);
                }
                catch { }
            }
        }
    }

    public void ReleaseHeldFlow(string flowKey, bool allow, bool remember = true)
    {
        if (string.IsNullOrEmpty(flowKey)) return;
        if (remember)
        {
            _heldDecisions[flowKey] = allow ? BlockerAction.Allow : BlockerAction.Deny;
        }

        var matches = _heldPackets.Values.Where(h => string.Equals(h.FlowKey, flowKey, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var h in matches)
        {
            ReleaseHeldPacket(h.Id, allow, rememberFlow: false);
        }
    }

    public void ReleaseHeldApp(string appName, bool allow, bool remember = true)
    {
        if (string.IsNullOrEmpty(appName)) return;
        if (remember)
        {
            _heldDecisions[appName.ToLowerInvariant()] = allow ? BlockerAction.Allow : BlockerAction.Deny;
        }

        var matches = _heldPackets.Values.Where(h => string.Equals(h.ProcessName, appName, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var h in matches)
        {
            ReleaseHeldPacket(h.Id, allow, rememberFlow: false);
        }
    }

    public void CleanupStaleHeldPackets(double maxAgeSeconds = 25.0)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-maxAgeSeconds);
        foreach (var kvp in _heldPackets)
        {
            if (kvp.Value.CreatedAtUtc < cutoff)
            {
                _heldPackets.TryRemove(kvp.Key, out _);
            }
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnavailableReason = "stopped";

        try { _heldPackets.Clear(); } catch { }
        try { _heldDecisions.Clear(); } catch { }
        try { _portRefreshTimer?.Dispose(); } catch { }

        StopWorkers();

        _pacingSignal.Dispose();
    }

    #region Internal Support Structures

    public sealed class ServicePidStats
    {
        public int pid { get; set; }
        public ulong @in { get; set; }
        public ulong @out { get; set; }
    }

    public sealed class ServiceStatsPayload
    {
        public bool shaperAvailable { get; set; }
        public string? shaperReason { get; set; }
        public ulong totalIn { get; set; }
        public ulong totalOut { get; set; }
        public double heartbeatMs { get; set; }
        public double pid0Ratio { get; set; }
        public List<ServicePidStats> perPid { get; set; } = new();
    }

    public sealed class HeldPacketDto
    {
        public ulong id { get; set; }
        public int pid { get; set; }
        public string processName { get; set; } = "";
        public string appPath { get; set; } = "";
        public string localIp { get; set; } = "";
        public int localPort { get; set; }
        public string remoteIp { get; set; } = "";
        public int remotePort { get; set; }
        public string protocol { get; set; } = "";
        public bool isInbound { get; set; }
        public string flowKey { get; set; } = "";
        public DateTime createdAtUtc { get; set; }
    }

    public sealed class ResolvePacketPayload
    {
        public ulong packetId { get; set; }
        public bool allow { get; set; }
        public bool rememberFlow { get; set; } = true;
        public int durationSeconds { get; set; }
    }

    public ServiceStatsPayload GetStatsSnapshot(int topCount = 50)
    {
        TryGetTotalStats(out ulong totalIn, out ulong totalOut);
        double hb = (DateTime.UtcNow - LastPacketUtc).TotalMilliseconds;
        var pids = new HashSet<int>(_bytesIn.Keys.Concat(_bytesOut.Keys));
        var list = new List<ServicePidStats>(pids.Count);
        foreach (int pid in pids)
        {
            _bytesIn.TryGetValue(pid, out ulong inBytes);
            _bytesOut.TryGetValue(pid, out ulong outBytes);
            list.Add(new ServicePidStats { pid = pid, @in = inBytes, @out = outBytes });
        }
        var top = list.OrderByDescending(p => p.@in + p.@out).Take(topCount).ToList();
        return new ServiceStatsPayload
        {
            shaperAvailable = IsAvailable,
            shaperReason = UnavailableReason,
            totalIn = totalIn,
            totalOut = totalOut,
            heartbeatMs = hb,
            pid0Ratio = Pid0Ratio,
            perPid = top
        };
    }

    public List<HeldPacketDto> GetHeldPacketsDto()
    {
        var list = new List<HeldPacketDto>();
        foreach (var hp in _heldPackets.Values)
        {
            list.Add(new HeldPacketDto
            {
                id = hp.Id,
                pid = hp.Pid,
                processName = hp.ProcessName,
                appPath = hp.AppPath,
                localIp = hp.LocalIp,
                localPort = hp.LocalPort,
                remoteIp = hp.RemoteIp,
                remotePort = hp.RemotePort,
                protocol = hp.Protocol,
                isInbound = hp.IsInbound,
                flowKey = hp.FlowKey,
                createdAtUtc = hp.CreatedAtUtc
            });
        }
        return list;
    }

    public sealed class HeldPacket
    {
        public ulong Id { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
        public WinDivertAddress Address { get; init; } = new();
        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
        public int Pid { get; init; }
        public string ProcessName { get; init; } = "";
        public string AppPath { get; init; } = "";
        public string LocalIp { get; init; } = "";
        public int LocalPort { get; init; }
        public string RemoteIp { get; init; } = "";
        public int RemotePort { get; init; }
        public string Protocol { get; init; } = "";
        public bool IsInbound { get; init; }
        public string FlowKey { get; init; } = "";
    }

    public sealed class PacedPacket : IDisposable
    {
        public WinDivertPacket Packet { get; } = new WinDivertPacket(65535);
        public int Length { get; set; }
        public WinDivertAddress Address = new();
        public long ReleaseTicks { get; set; }
        public TrafficPriority Priority { get; set; }

        public void Dispose()
        {
            Packet.Dispose();
        }
    }

    public sealed class TokenBucket
    {
        private long _bytesPerSec;
        private long _capacity;
        private double _tokens;
        private long _lastTicks;
        private readonly object _lock = new();

        public long BytesPerSec => Interlocked.Read(ref _bytesPerSec);

        public TokenBucket(long bytesPerSec, long burstCapacityBytes = 0)
        {
            SetRate(bytesPerSec, burstCapacityBytes);
            _tokens = _capacity;
            _lastTicks = Stopwatch.GetTimestamp();
        }

        public void SetRate(long bytesPerSec, long burstCapacityBytes = 0)
        {
            lock (_lock)
            {
                _bytesPerSec = Math.Max(1024, bytesPerSec);
                long defaultCap = Math.Max(65536, _bytesPerSec / 10);
                _capacity = burstCapacityBytes > 0 ? burstCapacityBytes : defaultCap;
                if (_tokens > _capacity) _tokens = _capacity;
            }
        }

        public bool TryConsume(int bytes, out double delaySeconds)
        {
            long now = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                double elapsed = (double)(now - _lastTicks) / Stopwatch.Frequency;
                _lastTicks = now;
                _tokens = Math.Min(_capacity, _tokens + elapsed * _bytesPerSec);

                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    delaySeconds = 0;
                    return true;
                }

                double deficit = bytes - _tokens;
                delaySeconds = deficit / _bytesPerSec;

                // CRITICAL SAFETY FIX: If the packet would exceed the max queue threshold (0.200s),
                // it cannot be scheduled and will be dropped. Dropped packets MUST NOT consume tokens!
                // Also clamp token debt to floor so the bucket recovers instantly.
                if (delaySeconds > MaxQueueDelaySeconds)
                {
                    double floor = -(_bytesPerSec * MaxQueueDelaySeconds);
                    if (_tokens < floor) _tokens = floor;
                    return false;
                }

                _tokens -= bytes; // Enqueued packet consumes tokens
                return false;
            }
        }

        public bool Consume(int bytes) => TryConsume(bytes, out _);
    }

    #endregion
}
