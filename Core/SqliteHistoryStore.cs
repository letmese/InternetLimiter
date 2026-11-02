using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Valve.Core;

public sealed class HistoryRecordEntry
{
    public string App { get; set; } = string.Empty;
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public string? RemoteIp { get; set; }
    public DateTime Timestamp { get; set; }
}

public sealed class HourlyBucket
{
    public long Epoch { get; set; }
    public DateTime LabelTime { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public string TopApp { get; set; } = string.Empty;
}

public sealed class AppRankingSummary
{
    public string App { get; set; } = string.Empty;
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public double InPercentage { get; set; }
    public double OutPercentage { get; set; }
    public string DownloadText => Fmt.Bytes(BytesIn);
    public string UploadText => Fmt.Bytes(BytesOut);
    public string TotalText => Fmt.Bytes(BytesIn + BytesOut);
}

/// <summary>
/// Locally stored, persistent SQLite3 traffic statistics database backend (NetLimiter Sec 13, Item 13.01).
/// Replaces legacy proprietary/JSON formats with a standard, ACID-compliant SQLite database.
/// All write operations are non-blocking and queued via ConcurrentQueue, drained on a background task
/// every 1 second in atomic batched transactions.
/// </summary>
public sealed class SqliteHistoryStore : IDisposable
{
    private readonly string _dbPath;
    private readonly object _lock = new();
    private SqliteConnection? _connection;
    private bool _isDisposed;

    private readonly ConcurrentQueue<HistoryRecordEntry> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drainTask;
    private const int MaxQueueCapacity = 10000;
    private bool _hasWarnedDrop = false;

    public string DatabasePath => _dbPath;
    public int PendingQueueCount => _queue.Count;

    public SqliteHistoryStore(string? dbPath = null)
    {
        if (string.IsNullOrEmpty(dbPath))
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve");
            Directory.CreateDirectory(dir);
            _dbPath = Path.Combine(dir, "traffic_stats.db");
        }
        else
        {
            _dbPath = dbPath;
            string? dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        InitSchema();
        _drainTask = Task.Run(DrainLoopAsync);
    }

    private void InitSchema()
    {
        lock (_lock)
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };
            _connection = new SqliteConnection(csb.ConnectionString);
            _connection.Open();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;

                CREATE TABLE IF NOT EXISTS daily_app_traffic (
                    app TEXT NOT NULL,
                    date_str TEXT NOT NULL,
                    bytes_in INTEGER NOT NULL DEFAULT 0,
                    bytes_out INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (app, date_str)
                );

                CREATE TABLE IF NOT EXISTS hourly_app_traffic (
                    app TEXT NOT NULL,
                    hour_epoch INTEGER NOT NULL,
                    bytes_in INTEGER NOT NULL DEFAULT 0,
                    bytes_out INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (app, hour_epoch)
                );

                CREATE TABLE IF NOT EXISTS remote_endpoint_traffic (
                    app TEXT NOT NULL,
                    remote_ip TEXT NOT NULL,
                    date_str TEXT NOT NULL,
                    bytes_in INTEGER NOT NULL DEFAULT 0,
                    bytes_out INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (app, remote_ip, date_str)
                );

                CREATE INDEX IF NOT EXISTS idx_daily_date ON daily_app_traffic(date_str);
                CREATE INDEX IF NOT EXISTS idx_hourly_hour ON hourly_app_traffic(hour_epoch);
            ";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Non-blocking write: Enqueues a traffic usage sample to be committed asynchronously.
    /// Never touches disk or locks the SQLite connection from the calling UI thread.
    /// </summary>
    public void Record(string app, long bytesIn, long bytesOut, string? remoteIp = null, DateTime? timestamp = null)
    {
        if (bytesIn <= 0 && bytesOut <= 0) return;
        if (_isDisposed) return;

        // Cap queue at 10,000 entries — drop oldest + log warning if hit
        while (_queue.Count >= MaxQueueCapacity)
        {
            _queue.TryDequeue(out _);
            if (!_hasWarnedDrop)
            {
                _hasWarnedDrop = true;
                System.Diagnostics.Debug.WriteLine("[WARN] SqliteHistoryStore write queue reached capacity (10,000). Dropping oldest records.");
            }
        }

        _queue.Enqueue(new HistoryRecordEntry
        {
            App = app,
            BytesIn = bytesIn,
            BytesOut = bytesOut,
            RemoteIp = remoteIp,
            Timestamp = timestamp ?? DateTime.UtcNow
        });
    }

    private async Task DrainLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                DrainQueue();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] SqliteHistoryStore DrainLoop: {ex.Message}");
            }
        }
    }

    private void DrainQueue()
    {
        if (_queue.IsEmpty) return;

        var batch = new List<HistoryRecordEntry>();
        while (_queue.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }

        if (batch.Count == 0) return;

        lock (_lock)
        {
            if (_connection == null || _isDisposed) return;
            try
            {
                using var tx = _connection.BeginTransaction();

                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO daily_app_traffic (app, date_str, bytes_in, bytes_out)
                    VALUES (@app, @date_str, @in, @out)
                    ON CONFLICT(app, date_str) DO UPDATE SET
                        bytes_in = bytes_in + @in,
                        bytes_out = bytes_out + @out;

                    INSERT INTO hourly_app_traffic (app, hour_epoch, bytes_in, bytes_out)
                    VALUES (@app, @hour_epoch, @in, @out)
                    ON CONFLICT(app, hour_epoch) DO UPDATE SET
                        bytes_in = bytes_in + @in,
                        bytes_out = bytes_out + @out;
                ";
                var pApp = cmd.Parameters.Add("@app", SqliteType.Text);
                var pDate = cmd.Parameters.Add("@date_str", SqliteType.Text);
                var pHour = cmd.Parameters.Add("@hour_epoch", SqliteType.Integer);
                var pIn = cmd.Parameters.Add("@in", SqliteType.Integer);
                var pOut = cmd.Parameters.Add("@out", SqliteType.Integer);

                using var rcmd = _connection.CreateCommand();
                rcmd.Transaction = tx;
                rcmd.CommandText = @"
                    INSERT INTO remote_endpoint_traffic (app, remote_ip, date_str, bytes_in, bytes_out)
                    VALUES (@app, @remote_ip, @date_str, @in, @out)
                    ON CONFLICT(app, remote_ip, date_str) DO UPDATE SET
                        bytes_in = bytes_in + @in,
                        bytes_out = bytes_out + @out;
                ";
                var rpApp = rcmd.Parameters.Add("@app", SqliteType.Text);
                var rpIp = rcmd.Parameters.Add("@remote_ip", SqliteType.Text);
                var rpDate = rcmd.Parameters.Add("@date_str", SqliteType.Text);
                var rpIn = rcmd.Parameters.Add("@in", SqliteType.Integer);
                var rpOut = rcmd.Parameters.Add("@out", SqliteType.Integer);

                foreach (var entry in batch)
                {
                    DateTime dt = entry.Timestamp;
                    string dateStr = dt.ToString("yyyy-MM-dd");
                    long hourEpoch = new DateTimeOffset(new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    string normApp = string.IsNullOrEmpty(entry.App) ? "system" : entry.App.ToLowerInvariant();

                    pApp.Value = normApp;
                    pDate.Value = dateStr;
                    pHour.Value = hourEpoch;
                    pIn.Value = entry.BytesIn;
                    pOut.Value = entry.BytesOut;
                    cmd.ExecuteNonQuery();

                    if (!string.IsNullOrEmpty(entry.RemoteIp))
                    {
                        rpApp.Value = normApp;
                        rpIp.Value = entry.RemoteIp;
                        rpDate.Value = dateStr;
                        rpIn.Value = entry.BytesIn;
                        rpOut.Value = entry.BytesOut;
                        rcmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] SqliteHistoryStore transaction: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Synchronously drains and flushes all queued records to the database file within the given timeout.
    /// Used during clean application shutdown and in unit tests.
    /// </summary>
    public void FlushSync(int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_queue.IsEmpty && sw.ElapsedMilliseconds < timeoutMs)
        {
            DrainQueue();
            if (!_queue.IsEmpty)
            {
                Thread.Sleep(10);
            }
        }
        DrainQueue();
    }

    public List<DailyUsage> QueryDay(DateOnly day)
    {
        FlushSync(1000); // Ensure recent writes are visible before querying
        string dateStr = day.ToString("yyyy-MM-dd");
        var list = new List<DailyUsage>();
        lock (_lock)
        {
            if (_connection == null || _isDisposed) return list;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT app, bytes_in, bytes_out FROM daily_app_traffic WHERE date_str = @d";
                cmd.Parameters.AddWithValue("@d", dateStr);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new DailyUsage
                    {
                        App = reader.GetString(0),
                        BytesIn = reader.GetInt64(1),
                        BytesOut = reader.GetInt64(2)
                    });
                }
            }
            catch { }
        }
        return list;
    }

    public List<DailyUsage> QueryRange(DateOnly from, DateOnly to)
    {
        FlushSync(1000); // Ensure recent writes are visible before querying
        string fromStr = from.ToString("yyyy-MM-dd");
        string toStr = to.ToString("yyyy-MM-dd");
        var list = new List<DailyUsage>();
        lock (_lock)
        {
            if (_connection == null || _isDisposed) return list;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT app, SUM(bytes_in) AS total_in, SUM(bytes_out) AS total_out
                    FROM daily_app_traffic
                    WHERE date_str >= @from AND date_str <= @to
                    GROUP BY app;
                ";
                cmd.Parameters.AddWithValue("@from", fromStr);
                cmd.Parameters.AddWithValue("@to", toStr);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new DailyUsage
                    {
                        App = reader.GetString(0),
                        BytesIn = reader.GetInt64(1),
                        BytesOut = reader.GetInt64(2)
                    });
                }
            }
            catch { }
        }
        return list;
    }

    public (int DayCount, long TotalBytes) GetTotals()
    {
        FlushSync(1000); // Ensure recent writes are visible before querying
        lock (_lock)
        {
            if (_connection == null || _isDisposed) return (0, 0);
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(DISTINCT date_str), COALESCE(SUM(bytes_in + bytes_out), 0)
                    FROM daily_app_traffic;
                ";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    int days = reader.GetInt32(0);
                    long bytes = reader.GetInt64(1);
                    return (days, bytes);
                }
            }
            catch { }
        }
        return (0, 0);
    }

    public List<HourlyBucket> QueryHourlySeries(long startEpoch, long endEpoch, string? appFilter = null)
    {
        FlushSync(300);
        var result = new List<HourlyBucket>();
        lock (_lock)
        {
            if (_connection == null || _isDisposed) return result;
            try
            {
                using var cmd = _connection.CreateCommand();
                if (string.IsNullOrWhiteSpace(appFilter))
                {
                    cmd.CommandText = @"
                        SELECT hour_epoch, SUM(bytes_in) AS bin, SUM(bytes_out) AS bout
                        FROM hourly_app_traffic
                        WHERE hour_epoch >= @start AND hour_epoch <= @end
                        GROUP BY hour_epoch
                        ORDER BY hour_epoch ASC;
                    ";
                }
                else
                {
                    cmd.CommandText = @"
                        SELECT hour_epoch, SUM(bytes_in) AS bin, SUM(bytes_out) AS bout
                        FROM hourly_app_traffic
                        WHERE hour_epoch >= @start AND hour_epoch <= @end AND app = @app
                        GROUP BY hour_epoch
                        ORDER BY hour_epoch ASC;
                    ";
                    cmd.Parameters.AddWithValue("@app", appFilter.Trim().ToLowerInvariant());
                }
                cmd.Parameters.AddWithValue("@start", startEpoch);
                cmd.Parameters.AddWithValue("@end", endEpoch);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    long ep = reader.GetInt64(0);
                    long bin = reader.GetInt64(1);
                    long bout = reader.GetInt64(2);
                    result.Add(new HourlyBucket
                    {
                        Epoch = ep,
                        LabelTime = DateTimeOffset.FromUnixTimeSeconds(ep).LocalDateTime,
                        BytesIn = bin,
                        BytesOut = bout
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] QueryHourlySeries: {ex.Message}");
            }
        }
        return result;
    }

    public List<HourlyBucket> QueryDailySeries(DateOnly from, DateOnly to, string? appFilter = null)
    {
        FlushSync(300);
        var result = new List<HourlyBucket>();
        lock (_lock)
        {
            if (_connection == null || _isDisposed) return result;
            try
            {
                using var cmd = _connection.CreateCommand();
                if (string.IsNullOrWhiteSpace(appFilter))
                {
                    cmd.CommandText = @"
                        SELECT date_str, SUM(bytes_in) AS bin, SUM(bytes_out) AS bout
                        FROM daily_app_traffic
                        WHERE date_str >= @from AND date_str <= @to
                        GROUP BY date_str
                        ORDER BY date_str ASC;
                    ";
                }
                else
                {
                    cmd.CommandText = @"
                        SELECT date_str, SUM(bytes_in) AS bin, SUM(bytes_out) AS bout
                        FROM daily_app_traffic
                        WHERE date_str >= @from AND date_str <= @to AND app = @app
                        GROUP BY date_str
                        ORDER BY date_str ASC;
                    ";
                    cmd.Parameters.AddWithValue("@app", appFilter.Trim().ToLowerInvariant());
                }
                cmd.Parameters.AddWithValue("@from", from.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@to", to.ToString("yyyy-MM-dd"));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string ds = reader.GetString(0);
                    long bin = reader.GetInt64(1);
                    long bout = reader.GetInt64(2);
                    if (DateTime.TryParse(ds, out var dt))
                    {
                        long ep = new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds();
                        result.Add(new HourlyBucket
                        {
                            Epoch = ep,
                            LabelTime = dt,
                            BytesIn = bin,
                            BytesOut = bout
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] QueryDailySeries: {ex.Message}");
            }
        }
        return result;
    }

    public List<AppRankingSummary> QueryAppRankings(long? startEpoch = null, long? endEpoch = null, DateOnly? from = null, DateOnly? to = null)
    {
        FlushSync(300);
        var list = new List<AppRankingSummary>();
        lock (_lock)
        {
            if (_connection == null || _isDisposed) return list;
            try
            {
                using var cmd = _connection.CreateCommand();
                if (startEpoch.HasValue && endEpoch.HasValue)
                {
                    cmd.CommandText = @"
                        SELECT app, SUM(bytes_in) AS bin, SUM(bytes_out) AS bout
                        FROM hourly_app_traffic
                        WHERE hour_epoch >= @start AND hour_epoch <= @end
                        GROUP BY app;
                    ";
                    cmd.Parameters.AddWithValue("@start", startEpoch.Value);
                    cmd.Parameters.AddWithValue("@end", endEpoch.Value);
                }
                else
                {
                    string fStr = (from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-365))).ToString("yyyy-MM-dd");
                    string tStr = (to ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToString("yyyy-MM-dd");
                    cmd.CommandText = @"
                        SELECT app, SUM(bytes_in) AS bin, SUM(bytes_out) AS bout
                        FROM daily_app_traffic
                        WHERE date_str >= @from AND date_str <= @to
                        GROUP BY app;
                    ";
                    cmd.Parameters.AddWithValue("@from", fStr);
                    cmd.Parameters.AddWithValue("@to", tStr);
                }

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new AppRankingSummary
                    {
                        App = reader.GetString(0),
                        BytesIn = reader.GetInt64(1),
                        BytesOut = reader.GetInt64(2)
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] QueryAppRankings: {ex.Message}");
            }
        }
        return list;
    }

    public void MigrateFromJson(string jsonDir)
    {
        if (!Directory.Exists(jsonDir)) return;
        var files = Directory.GetFiles(jsonDir, "????-??-??.json");
        foreach (var file in files)
        {
            string dateStr = Path.GetFileNameWithoutExtension(file);
            try
            {
                string json = File.ReadAllText(file);
                var usages = System.Text.Json.JsonSerializer.Deserialize<List<DailyUsage>>(json);
                if (usages != null)
                {
                    foreach (var u in usages)
                    {
                        Record(u.App, u.BytesIn, u.BytesOut, null, DateTime.Parse(dateStr));
                    }
                }
            }
            catch { }
        }
        FlushSync();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _cts.Cancel();
            FlushSync(5000);
        }
        catch { }

        lock (_lock)
        {
            _connection?.Dispose();
            _connection = null;
        }

        try
        {
            _cts.Dispose();
        }
        catch { }
    }
}
