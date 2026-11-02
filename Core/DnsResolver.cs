using System.Collections.Concurrent;
using System.Net;

namespace Valve.Core;

/// <summary>
/// Background asynchronous reverse DNS resolver with memory cache (Section 8.07, 10.01.d).
/// Resolves remote IPv4 and IPv6 addresses to hostnames without blocking the UI or collector.
/// </summary>
public sealed class DnsResolver : IDisposable
{
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _disposed;

    public event Action<string, string>? Resolved;

    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Valve-DnsResolver",
        };
        _worker.Start();
    }

    public string Lookup(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "";
        return _cache.TryGetValue(ip, out string? host) ? host : "";
    }

    public void Enqueue(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || _disposed) return;
        if (GeoIp.IsPrivate(ip)) return;
        if (_cache.ContainsKey(ip)) return;

        lock (_gate)
        {
            if (_queued.Add(ip))
            {
                _pending.Enqueue(ip);
                Monitor.Pulse(_gate);
            }
        }
    }

    private void WorkerLoop()
    {
        while (_running)
        {
            string? ip = null;
            lock (_gate)
            {
                while (_pending.IsEmpty && _running)
                {
                    Monitor.Wait(_gate, 500);
                }

                if (!_running) break;
                _pending.TryDequeue(out ip);
            }

            if (ip is null) continue;
            if (_cache.Count > 10000) _cache.Clear();

            try
            {
                IPHostEntry entry = Dns.GetHostEntry(ip);
                string hostName = entry.HostName ?? "";
                if (!string.IsNullOrWhiteSpace(hostName))
                {
                    _cache[ip] = hostName;
                    Resolved?.Invoke(ip, hostName);
                }
                else
                {
                    _cache[ip] = ip;
                }
            }
            catch
            {
                _cache[ip] = ip; // Avoid re-querying failed lookups constantly
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }
}
