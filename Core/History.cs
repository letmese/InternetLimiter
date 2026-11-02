using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Valve.Core;

/// <summary>Traffic attributed to one application on one local day.</summary>
public sealed class DailyUsage
{
    public string App { get; set; } = "";      // lowercase exe name
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
}

/// <summary>
/// Rolling per-application traffic history. One JSON file per local date under
/// %LOCALAPPDATA%\Valve\history. Writes are buffered and flushed at most every
/// 30 seconds so the per-second collector never waits on the disk.
/// </summary>
public sealed class HistoryStore
{
    public int RetentionDays { get; set; } = 0; // 0 = Keep forever (unlimited)
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private const string DateFormat = "yyyy-MM-dd";

    private readonly string _root;
    private readonly object _gate = new();

    /// <summary>Underlying persistent SQLite3 statistics database (Item 13.01).</summary>
    public SqliteHistoryStore SqliteStore { get; }

    /// <summary>Every day we have loaded, including the open day. Guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<DateOnly, Dictionary<string, DailyUsage>> _cache = new();

    private DateOnly _openDate;
    private Dictionary<string, DailyUsage> _open;
    private DateTime _lastFlushUtc;

    public HistoryStore(string? rootDirectory = null)
    {
        _root = rootDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve", "history");

        try
        {
            Directory.CreateDirectory(_root);
            RepairDatabase();
        }
        catch (Exception)
        {
            // An unwritable history directory must not stop the app from starting.
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        _openDate = today;
        _open = LoadDay(today);      // continue whatever was already recorded today
        _cache[today] = _open;
        _lastFlushUtc = DateTime.UtcNow;

        string baseDir = !string.IsNullOrEmpty(rootDirectory) ? Path.GetDirectoryName(rootDirectory)! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve");
        SqliteStore = new SqliteHistoryStore(Path.Combine(baseDir, "traffic_stats.db"));
        try { SqliteStore.MigrateFromJson(_root); } catch { }
    }

    /// <summary>Adds a delta. Thread-safe, called once per second from a worker thread.</summary>
    public void Record(string app, long deltaIn, long deltaOut)
    {
        string key = Normalize(app);
        if (key.Length == 0)
        {
            return;
        }

        // Deltas come from counter differences; a negative value means the counter
        // was reset, so it is not traffic.
        long inBytes = deltaIn > 0 ? deltaIn : 0;
        long outBytes = deltaOut > 0 ? deltaOut : 0;

        if (inBytes > 0 || outBytes > 0)
        {
            try { SqliteStore.Record(key, inBytes, outBytes); } catch { }
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        // Fast pre-check so the new day's file is read outside the lock. Re-checked below.
        Dictionary<string, DailyUsage>? preload = null;
        if (today != _openDate)
        {
            preload = LoadDay(today);
        }

        Dictionary<string, DailyUsage>? closed = null;
        DateOnly closedDate = default;
        Dictionary<string, DailyUsage>? due = null;
        DateOnly dueDate = default;

        lock (_gate)
        {
            if (today != _openDate)
            {
                closed = Snapshot(_open);
                closedDate = _openDate;
                _openDate = today;
                _open = preload ?? LoadDay(today);
                _cache[today] = _open;
            }

            if (inBytes > 0 || outBytes > 0)
            {
                if (!_open.TryGetValue(key, out DailyUsage? usage))
                {
                    usage = new DailyUsage { App = key };
                    _open[key] = usage;
                }

                usage.BytesIn += inBytes;
                usage.BytesOut += outBytes;
            }

            DateTime nowUtc = DateTime.UtcNow;
            if (nowUtc - _lastFlushUtc >= FlushInterval)
            {
                _lastFlushUtc = nowUtc;
                due = Snapshot(_open);
                dueDate = _openDate;
            }
        }

        // All file I/O happens outside the lock. Failures are swallowed by the helpers,
        // and the in-memory buckets stay intact so a later flush can still succeed.
        if (closed is not null)
        {
            WriteDay(closedDate, closed);
            Prune(closedDate.AddDays(1));
        }

        if (due is not null)
        {
            WriteDay(dueDate, due);
        }
    }

    /// <summary>Per-app totals for a given local date.</summary>
    public IReadOnlyList<DailyUsage> GetDay(DateOnly date)
    {
        Dictionary<string, DailyUsage> bucket = Bucket(date);
        List<DailyUsage> list;

        lock (_gate)
        {
            list = new List<DailyUsage>(bucket.Count);
            foreach (DailyUsage usage in bucket.Values)
            {
                list.Add(Copy(usage));
            }
        }

        Sort(list);
        return list;
    }

    /// <summary>Clears one application's recorded usage for one date (quota reset, 7.07.c).</summary>
    public void DeleteAppDay(string app, DateOnly date)
    {
        string key = Normalize(app);
        if (key.Length == 0)
        {
            return;
        }

        Dictionary<string, DailyUsage> bucket = Bucket(date);

        lock (_gate)
        {
            bucket.Remove(key);
            if (date == _openDate && ReferenceEquals(bucket, _open))
            {
                WriteDay(date, Snapshot(_open));
            }
            else
            {
                WriteDay(date, bucket);
            }
        }
    }

    /// <summary>Earliest and latest dates that have recorded history (Stats Manager).</summary>
    public (DateOnly? First, DateOnly? Last) GetDateRange()
    {
        DateOnly? first = null;
        DateOnly? last = null;

        try
        {
            if (!Directory.Exists(_root))
            {
                return (first, last);
            }

            lock (_gate)
            {
                foreach (DateOnly date in _cache.Keys)
                {
                    if (first is null || date < first)
                    {
                        first = date;
                    }

                    if (last is null || date > last)
                    {
                        last = date;
                    }
                }
            }

            foreach (string file in Directory.EnumerateFiles(_root, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                {
                    if (first is null || date < first)
                    {
                        first = date;
                    }

                    if (last is null || date > last)
                    {
                        last = date;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Unreadable directory: return what we have.
        }

        return (first, last);
    }

    /// <summary>Deletes every day file whose date falls inside [start, end] (Stats Manager).</summary>
    public void ClearRange(DateOnly start, DateOnly end)
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(_root, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                {
                    continue;
                }

                if (date < start || date > end)
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    string bak = file + ".bak";
                    if (File.Exists(bak))
                    {
                        File.Delete(bak);
                    }
                }
                catch (Exception)
                {
                    // Locked file: memory copy still wins and gets rewritten later.
                }
            }
        }
        catch (Exception)
        {
            // Enumeration failure is not worth surfacing.
        }

        lock (_gate)
        {
            var stale = new List<DateOnly>();
            foreach (DateOnly date in _cache.Keys)
            {
                if (date >= start && date <= end && date != _openDate)
                {
                    stale.Add(date);
                }
            }

            foreach (DateOnly date in stale)
            {
                _cache.Remove(date);
            }

            if (start <= _openDate && end >= _openDate)
            {
                _open.Clear();
                WriteDay(_openDate, Snapshot(_open));
            }
        }
    }

    /// <summary>Removes one application from every recorded day (Stats Manager).</summary>
    public void DeleteAppAll(string app)
    {
        string key = Normalize(app);
        if (key.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            var touched = new List<DateOnly>();

            foreach (KeyValuePair<DateOnly, Dictionary<string, DailyUsage>> day in _cache)
            {
                if (day.Value.Remove(key))
                {
                    touched.Add(day.Key);
                }
            }

            foreach (DateOnly date in touched)
            {
                WriteDay(date, Snapshot(_cache[date]));
            }
        }
    }

    /// <summary>Sums the recorded usage of several apps into one target app (Stats Manager merge).</summary>
    public void MergeApps(string target, IReadOnlyList<string> sources)
    {
        string targetKey = Normalize(target);
        if (targetKey.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            var touched = new List<DateOnly>();

            foreach (KeyValuePair<DateOnly, Dictionary<string, DailyUsage>> day in _cache)
            {
                bool changed = false;

                foreach (string source in sources)
                {
                    string sourceKey = Normalize(source);
                    if (sourceKey.Length == 0 || sourceKey == targetKey)
                    {
                        continue;
                    }

                    if (!day.Value.TryGetValue(sourceKey, out DailyUsage? usage))
                    {
                        continue;
                    }

                    if (!day.Value.TryGetValue(targetKey, out DailyUsage? dst))
                    {
                        dst = new DailyUsage { App = targetKey };
                        day.Value[targetKey] = dst;
                    }

                    dst.BytesIn += usage.BytesIn;
                    dst.BytesOut += usage.BytesOut;
                    day.Value.Remove(sourceKey);
                    changed = true;
                }

                if (changed)
                {
                    touched.Add(day.Key);
                }
            }

            foreach (DateOnly date in touched)
            {
                WriteDay(date, Snapshot(_cache[date]));
            }
        }
    }

    /// <summary>Copies every valid day file from another Valve history directory (Stats Manager import).</summary>
    public int ImportFromDirectory(string sourceRoot)
    {
        int imported = 0;

        try
        {
            if (!Directory.Exists(sourceRoot))
            {
                return 0;
            }

            foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                {
                    continue;
                }

                try
                {
                    string text = File.ReadAllText(file);
                    if (!AtomicStorage.IsValidPayload(text))
                    {
                        continue;
                    }

                    AtomicStorage.SaveAtomic(PathFor(date), text);

                    lock (_gate)
                    {
                        if (_cache.TryGetValue(date, out Dictionary<string, DailyUsage>? cached))
                        {
                            Dictionary<string, DailyUsage> loaded = LoadDay(date);
                            if (loaded.Count > 0)
                            {
                                _cache[date] = loaded;
                            }
                        }
                    }

                    imported++;
                }
                catch (Exception)
                {
                    // One bad file must not stop the whole import.
                }
            }
        }
        catch (Exception)
        {
            // Enumeration failure is not worth surfacing.
        }

        return imported;
    }

    /// <summary>Aggregated per-app totals across every recorded day (Stats Manager list).</summary>
    public IReadOnlyList<DailyUsage> GetAllApps()
    {
        var totals = new Dictionary<string, DailyUsage>(StringComparer.Ordinal);

        try
        {
            if (Directory.Exists(_root))
            {
                foreach (string file in Directory.EnumerateFiles(_root, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                    {
                        continue;
                    }

                    Dictionary<string, DailyUsage> bucket = Bucket(date);
                    lock (_gate)
                    {
                        foreach (DailyUsage usage in bucket.Values)
                        {
                            if (!totals.TryGetValue(usage.App, out DailyUsage? sum))
                            {
                                sum = new DailyUsage { App = usage.App };
                                totals[usage.App] = sum;
                            }

                            sum.BytesIn += usage.BytesIn;
                            sum.BytesOut += usage.BytesOut;
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Unreadable directory: return what we have.
        }

        var list = new List<DailyUsage>(totals.Values);
        Sort(list);
        return list;
    }

    /// <summary>Rewrites every day file through the atomic writer (compact/defrag).</summary>
    public int CompactAll()
    {
        int written = 0;

        try
        {
            if (!Directory.Exists(_root))
            {
                return 0;
            }

            foreach (string file in Directory.EnumerateFiles(_root, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                {
                    continue;
                }

                try
                {
                    Dictionary<string, DailyUsage> bucket = Bucket(date);
                    lock (_gate)
                    {
                        WriteDay(date, Snapshot(bucket));
                        string bak = file + ".bak";
                        if (File.Exists(bak))
                        {
                            File.Delete(bak);
                        }
                    }

                    written++;
                }
                catch (Exception)
                {
                    // Locked file: keep going.
                }
            }
        }
        catch (Exception)
        {
            // Enumeration failure is not worth surfacing.
        }

        return written;
    }

    /// <summary>Per-app totals summed over the last N days including today. days&lt;1 means all recorded history.</summary>
    public IReadOnlyList<DailyUsage> GetRange(int days)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var totals = new Dictionary<string, DailyUsage>(StringComparer.Ordinal);

        foreach (DateOnly day in EnumerateDates(days, today))
        {
            Dictionary<string, DailyUsage> bucket = Bucket(day);

            lock (_gate)
            {
                foreach (KeyValuePair<string, DailyUsage> pair in bucket)
                {
                    if (!totals.TryGetValue(pair.Key, out DailyUsage? sum))
                    {
                        sum = new DailyUsage { App = pair.Value.App };
                        totals[pair.Key] = sum;
                    }

                    sum.BytesIn += pair.Value.BytesIn;
                    sum.BytesOut += pair.Value.BytesOut;
                }
            }
        }

        var list = new List<DailyUsage>(totals.Values);
        Sort(list);
        return list;
    }

    /// <summary>Total bytes in/out across all apps for the last N days. days&lt;1 means all recorded history.</summary>
    public (long In, long Out) GetTotal(int days)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        long totalIn = 0;
        long totalOut = 0;

        foreach (DateOnly day in EnumerateDates(days, today))
        {
            Dictionary<string, DailyUsage> bucket = Bucket(day);

            lock (_gate)
            {
                foreach (DailyUsage usage in bucket.Values)
                {
                    totalIn += usage.BytesIn;
                    totalOut += usage.BytesOut;
                }
            }
        }

        return (totalIn, totalOut);
    }

    /// <summary>Resolves the date range for a stats window: last N days, or every recorded day when N&lt;1.</summary>
    private IEnumerable<DateOnly> EnumerateDates(int days, DateOnly today)
    {
        if (days >= 1)
        {
            for (int offset = 0; offset < days; offset++)
            {
                yield return today.AddDays(-offset);
            }
            yield break;
        }

        (DateOnly? first, _) = GetDateRange();
        if (first is null)
        {
            yield break;
        }

        for (DateOnly day = first.Value; day <= today; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    /// <summary>Total bytes in/out across all apps from a start date (inclusive) to today.</summary>
    public IReadOnlyList<DailyUsage> GetRangeSince(DateOnly start)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var totals = new Dictionary<string, DailyUsage>(StringComparer.Ordinal);

        for (DateOnly day = start; day <= today; day = day.AddDays(1))
        {
            Dictionary<string, DailyUsage> bucket = Bucket(day);

            lock (_gate)
            {
                foreach (KeyValuePair<string, DailyUsage> pair in bucket)
                {
                    if (!totals.TryGetValue(pair.Key, out DailyUsage? sum))
                    {
                        sum = new DailyUsage { App = pair.Value.App };
                        totals[pair.Key] = sum;
                    }

                    sum.BytesIn += pair.Value.BytesIn;
                    sum.BytesOut += pair.Value.BytesOut;
                }
            }
        }

        var list = new List<DailyUsage>(totals.Values);
        Sort(list);
        return list;
    }

    /// <summary>Total on-disk size of the history database (JSON day files + backups).</summary>
    public long DatabaseSizeBytes()
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(_root))
            {
                return 0;
            }

            foreach (string file in Directory.EnumerateFiles(_root))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    // Skip unreadable file.
                }
            }
        }
        catch (Exception)
        {
            // Enumeration failure is not worth surfacing.
        }

        return total;
    }

    /// <summary>Forces a synchronous flush to disk. Called on shutdown.</summary>
    public void Flush()
    {
        Dictionary<string, DailyUsage> snapshot;
        DateOnly date;

        lock (_gate)
        {
            snapshot = Snapshot(_open);
            date = _openDate;
            _lastFlushUtc = DateTime.UtcNow;
        }

        WriteDay(date, snapshot);
    }

    // ---------------------------------------------------------------- internals

    private static string Normalize(string? app)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            return string.Empty;
        }

        return app.Trim().ToLowerInvariant();
    }

    private static DailyUsage Copy(DailyUsage source)
        => new() { App = source.App, BytesIn = source.BytesIn, BytesOut = source.BytesOut };

    /// <summary>Detached copy so serialisation can run without holding <see cref="_gate"/>.</summary>
    private static Dictionary<string, DailyUsage> Snapshot(Dictionary<string, DailyUsage> source)
    {
        var copy = new Dictionary<string, DailyUsage>(source.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, DailyUsage> pair in source)
        {
            copy[pair.Key] = Copy(pair.Value);
        }

        return copy;
    }

    private static void Sort(List<DailyUsage> list)
        => list.Sort(static (left, right) =>
        {
            long leftTotal = left.BytesIn + left.BytesOut;
            long rightTotal = right.BytesIn + right.BytesOut;
            int byTotal = rightTotal.CompareTo(leftTotal);
            return byTotal != 0 ? byTotal : string.CompareOrdinal(left.App, right.App);
        });

    /// <summary>Returns the bucket for a date, loading it from disk on first access.</summary>
    private Dictionary<string, DailyUsage> Bucket(DateOnly date)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(date, out Dictionary<string, DailyUsage>? cached))
            {
                return cached;
            }
        }

        Dictionary<string, DailyUsage> loaded = LoadDay(date);

        lock (_gate)
        {
            if (_cache.TryGetValue(date, out Dictionary<string, DailyUsage>? raced))
            {
                return raced;
            }

            _cache[date] = loaded;
            return loaded;
        }
    }

    private string PathFor(DateOnly date)
        => Path.Combine(_root, date.ToString(DateFormat, CultureInfo.InvariantCulture) + ".json");

    /// <summary>Auto-recovers from power cuts or torn writes and cleans orphaned temp files.</summary>
    public void RepairDatabase()
    {
        try
        {
            if (!Directory.Exists(_root)) return;

            // 1. Recover any corrupted (0-byte or torn) .json files from .bak
            var jsonFiles = Directory.GetFiles(_root, "*.json");
            foreach (var file in jsonFiles)
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length == 0)
                    {
                        string bak = file + ".bak";
                        if (File.Exists(bak) && new FileInfo(bak).Length > 0)
                        {
                            File.Copy(bak, file, overwrite: true);
                        }
                    }
                }
                catch { }
            }

            // 2. Clean or recover from orphaned .tmp files left by unexpected shutdowns
            var tmpFiles = Directory.GetFiles(_root, "*.tmp");
            foreach (var tmp in tmpFiles)
            {
                try
                {
                    string name = Path.GetFileName(tmp);
                    int idx = name.IndexOf(".json.", StringComparison.OrdinalIgnoreCase);
                    if (idx > 0)
                    {
                        string baseJson = name.Substring(0, idx + 5);
                        string targetPath = Path.Combine(_root, baseJson);
                        if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
                        {
                            string text = File.ReadAllText(tmp);
                            if (AtomicStorage.IsValidPayload(text))
                            {
                                AtomicStorage.SaveAtomic(targetPath, text);
                            }
                        }
                    }
                    File.Delete(tmp);
                }
                catch { }
            }
        }
        catch { }
    }

    private Dictionary<string, DailyUsage> LoadDay(DateOnly date)
    {
        var bucket = new Dictionary<string, DailyUsage>(StringComparer.Ordinal);

        try
        {
            string path = PathFor(date);
            string? json = AtomicStorage.LoadAtomic(path, out _);
            if (string.IsNullOrEmpty(json))
            {
                return bucket;
            }

            Dictionary<string, DailyUsage>? loaded = JsonSerializer.Deserialize<Dictionary<string, DailyUsage>>(json, Options);
            if (loaded is null)
            {
                return bucket;
            }

            foreach (KeyValuePair<string, DailyUsage> pair in loaded)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                string key = Normalize(pair.Key);
                if (key.Length == 0)
                {
                    key = Normalize(pair.Value.App);
                }

                if (key.Length == 0)
                {
                    continue;
                }

                if (!bucket.TryGetValue(key, out DailyUsage? usage))
                {
                    usage = new DailyUsage { App = key };
                    bucket[key] = usage;
                }

                usage.BytesIn += pair.Value.BytesIn;
                usage.BytesOut += pair.Value.BytesOut;
            }
        }
        catch (Exception)
        {
            // A corrupt or locked day file reads as empty; memory wins over disk.
        }

        return bucket;
    }

    /// <summary>Serialises to disk with hardware write-through, then atomically swaps it in with backup protection.</summary>
    private void WriteDay(DateOnly date, Dictionary<string, DailyUsage> bucket)
    {
        string path = PathFor(date);

        try
        {
            string json = JsonSerializer.Serialize(bucket, Options);
            AtomicStorage.SaveAtomic(path, json);
        }
        catch (Exception)
        {
            // Locked / permission-denied files must not crash the app. The bucket is
            // still in memory, so the next flush gets another chance.
        }
    }

    /// <summary>Drops day files (and cache entries) older than the retention window.</summary>
    public void Prune(DateOnly today)
    {
        if (RetentionDays <= 0)
        {
            // RetentionDays <= 0 means Unlimited / Keep Forever: never delete old data
            return;
        }

        DateOnly cutoff = today.AddDays(-(RetentionDays - 1));

        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                {
                    continue;
                }

                if (date >= cutoff)
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    string bak = file + ".bak";
                    if (File.Exists(bak))
                    {
                        File.Delete(bak);
                    }
                }
                catch (Exception)
                {
                    // Locked file: it will be retried on the next rollover.
                }
            }
        }
        catch (Exception)
        {
            // Enumeration failure is not worth surfacing.
        }

        lock (_gate)
        {
            var stale = new List<DateOnly>();
            foreach (DateOnly date in _cache.Keys)
            {
                if (date < cutoff && date != _openDate)
                {
                    stale.Add(date);
                }
            }

            foreach (DateOnly date in stale)
            {
                _cache.Remove(date);
            }
        }
    }

    /// <summary>Gets metrics on the history database: count of days stored and total bytes on disk.</summary>
    public (int DayCount, long TotalBytes) GetStorageStats()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return (0, 0);
            }

            var files = Directory.GetFiles(_root, "*.json");
            long bytes = 0;
            foreach (string file in files)
            {
                try
                {
                    var fi = new FileInfo(file);
                    bytes += fi.Length;
                }
                catch { }
            }
            return (files.Length, bytes);
        }
        catch
        {
            return (0, 0);
        }
    }
}

/// <summary>Traffic attributed to one remote IP address for one application.</summary>
public sealed class RemoteIpUsage
{
    public string App { get; set; } = "";        // lowercase exe name
    public string RemoteIp { get; set; } = "";   // numeric IP only (no port)
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public int ConnCount { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// Per-remote-IP traffic history, same rolling JSON layout as HistoryStore
/// (%LOCALAPPDATA%\Valve\history\remoteip\yyyy-MM-dd.json). This is the real
/// backing store behind the statistics drill-down: aggregated per (app, IP)
/// rows surfaced through <see cref="EntityStatsRow"/> on the stats tab.
/// </summary>
public sealed class RemoteIpHistoryStore
{
    private const string DateFormat = "yyyy-MM-dd";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<DateOnly, Dictionary<string, RemoteIpUsage>> _cache = new();
    private DateOnly _openDate;
    private Dictionary<string, RemoteIpUsage> _open;

    public RemoteIpHistoryStore(string? rootDirectory = null)
    {
        _root = rootDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve", "history", "remoteip");
        try
        {
            Directory.CreateDirectory(_root);
        }
        catch (Exception)
        {
            // Unwritable history must not stop the app.
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        _openDate = today;
        _open = LoadDay(today);
        _cache[today] = _open;
    }

    private static string Key(string app, string remoteIp) => app + "|" + remoteIp;

    private Dictionary<string, RemoteIpUsage> LoadDay(DateOnly date)
    {
        string path = Path.Combine(_root, date.ToString(DateFormat) + ".json");
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, RemoteIpUsage>>(json, Options) ?? new();
            }
        }
        catch (Exception)
        {
            // Corrupt day file: start that day fresh rather than crash.
        }

        return new Dictionary<string, RemoteIpUsage>(StringComparer.Ordinal);
    }

    /// <summary>Records a per-second delta for one app to one remote IP. Thread-safe.</summary>
    public void Record(string app, string remoteIp, long deltaIn, long deltaOut, int connCount, DateTime? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(remoteIp) || remoteIp == "*")
        {
            return;
        }

        DateTime now = utcNow ?? DateTime.UtcNow;
        DateOnly today = DateOnly.FromDateTime(now);
        string key = Key(app, remoteIp);

        lock (_gate)
        {
            if (today != _openDate)
            {
                _openDate = today;
                _open = LoadDay(today);
                _cache[today] = _open;
            }

            if (!_open.TryGetValue(key, out RemoteIpUsage? usage))
            {
                usage = new RemoteIpUsage { App = app, RemoteIp = remoteIp, FirstSeenUtc = now };
                _open[key] = usage;
            }

            usage.BytesIn += deltaIn;
            usage.BytesOut += deltaOut;
            usage.ConnCount += connCount;
            usage.LastSeenUtc = now;
        }
    }

    /// <summary>Aggregated usage per (app, remote IP) across the last N days (including today).</summary>
    public IReadOnlyList<RemoteIpUsage> GetRecent(int days, DateTime? utcNow = null)
    {
        DateTime now = utcNow ?? DateTime.UtcNow;
        DateOnly cutoff = DateOnly.FromDateTime(now).AddDays(-(days - 1));
        var map = new Dictionary<string, RemoteIpUsage>(StringComparer.Ordinal);

        lock (_gate)
        {
            foreach ((DateOnly date, Dictionary<string, RemoteIpUsage> day) in _cache)
            {
                if (date < cutoff)
                {
                    continue;
                }

                foreach ((string key, RemoteIpUsage usage) in day)
                {
                    if (!map.TryGetValue(key, out RemoteIpUsage? agg))
                    {
                        agg = new RemoteIpUsage
                        {
                            App = usage.App,
                            RemoteIp = usage.RemoteIp,
                            FirstSeenUtc = usage.FirstSeenUtc,
                        };
                        map[key] = agg;
                    }

                    agg.BytesIn += usage.BytesIn;
                    agg.BytesOut += usage.BytesOut;
                    agg.ConnCount += usage.ConnCount;
                    if (usage.LastSeenUtc > agg.LastSeenUtc)
                    {
                        agg.LastSeenUtc = usage.LastSeenUtc;
                    }
                }
            }
        }

        return map.Values.ToList();
    }

    /// <summary>Flushes today's buffer to disk. Called on app shutdown and every minute.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            string path = Path.Combine(_root, _openDate.ToString(DateFormat) + ".json");
            try
            {
                Directory.CreateDirectory(_root);
                string json = JsonSerializer.Serialize(_open, Options);
                File.WriteAllText(path + ".tmp", json);
                if (File.Exists(path))
                {
                    File.Copy(path, path + ".bak", overwrite: true);
                }

                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception)
            {
                // Keep the in-memory day so nothing is lost; retried on next flush.
            }
        }
    }
}
