using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valve.Core;

public sealed record GeoLocation(string CountryCode, string Country, string City)
{
    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(City) ? Country : $"{City}, {Country}";

    [JsonIgnore]
    public bool IsLocal => CountryCode == "LO";

    [JsonIgnore]
    public string FlagEmoji => GeoIp.CountryCodeToFlagEmoji(CountryCode);

    [JsonIgnore]
    public string DisplayWithFlag => IsLocal ? "🏠 Local network" : (CountryCode == "??" ? Display : $"{FlagEmoji} {Display}");
}

/// <summary>
/// Resolves remote addresses to a country/city, the way NetLimiter labels its
/// connection list. Results are cached to disk so an address is only ever looked
/// up once. Private and loopback addresses are marked local and never sent out.
///
/// Uses the free ip-api.com batch endpoint: 100 addresses per request, no key.
/// It is a third-party call, so it is switchable off in settings and every
/// failure degrades to "unknown" rather than blocking the UI.
/// </summary>
public sealed class GeoIp : IDisposable
{
    private static readonly GeoLocation Local = new("LO", "Local network", "");
    private static readonly GeoLocation Unknown = new("??", "Unknown", "");

    public static string CountryCodeToFlagEmoji(string countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
        {
            return "🌐";
        }

        countryCode = countryCode.ToUpperInvariant();
        if (countryCode[0] < 'A' || countryCode[0] > 'Z' || countryCode[1] < 'A' || countryCode[1] > 'Z')
        {
            return "🌐";
        }

        int first = 0x1F1E6 + (countryCode[0] - 'A');
        int second = 0x1F1E6 + (countryCode[1] - 'A');
        return char.ConvertFromUtf32(first) + char.ConvertFromUtf32(second);
    }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly ConcurrentDictionary<string, GeoLocation> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _queueGate = new();
    private readonly string _cachePath;

    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _dirty;

    public GeoIp(string? rootDirectory = null)
    {
        string root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve");
        Directory.CreateDirectory(root);
        _cachePath = Path.Combine(root, "geoip.json");
        LoadCache();
    }

    public bool Enabled { get; set; } = true;

    /// <summary>Raised on a worker thread when an address resolves.</summary>
    public event Action<string, GeoLocation>? Resolved;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _worker = new Thread(Work) { IsBackground = true, Name = "geoip" };
        _worker.Start();
    }

    public GeoLocation Lookup(string address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return Unknown;
        }

        if (IsPrivate(address))
        {
            return Local;
        }

        return _cache.TryGetValue(address, out GeoLocation? known) ? known : Unknown;
    }

    /// <summary>Queues an address for resolution. Cheap and non-blocking.</summary>
    public void Enqueue(string address)
    {
        if (!Enabled || string.IsNullOrEmpty(address) || IsPrivate(address) || _cache.ContainsKey(address))
        {
            return;
        }

        lock (_queueGate)
        {
            if (!_queued.Add(address))
            {
                return;
            }
        }

        _pending.Enqueue(address);
    }

    private void Work()
    {
        while (_running)
        {
            var batch = new List<string>(100);
            while (batch.Count < 100 && _pending.TryDequeue(out string? ip))
            {
                batch.Add(ip);
            }

            if (batch.Count == 0)
            {
                if (_dirty)
                {
                    SaveCache();
                    _dirty = false;
                }

                Thread.Sleep(700);
                continue;
            }

            try
            {
                ResolveBatch(batch);
            }
            catch (Exception)
            {
                // Offline, rate limited, or the service changed shape. Leave the
                // addresses as Unknown and let them be retried later.
            }
            finally
            {
                lock (_queueGate)
                {
                    foreach (string ip in batch)
                    {
                        _queued.Remove(ip);
                    }
                }
            }
        }
    }

    private void ResolveBatch(List<string> batch)
    {
        string payload = JsonSerializer.Serialize(batch.Select(ip => new { query = ip }));
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = _http
            .PostAsync("https://ip-api.com/batch?fields=status,country,countryCode,city,query", content)
            .GetAwaiter()
            .GetResult();

        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        List<IpApiResult>? results = JsonSerializer.Deserialize<List<IpApiResult>>(body);
        if (results is null)
        {
            return;
        }

        foreach (IpApiResult item in results)
        {
            if (item.Query is null)
            {
                continue;
            }

            GeoLocation location = item.Status == "success"
                ? new GeoLocation(item.CountryCode ?? "??", item.Country ?? "Unknown", item.City ?? "")
                : Unknown;

            _cache[item.Query] = location;
            _dirty = true;
            Resolved?.Invoke(item.Query, location);
        }
    }

    public static bool IsPrivate(string address)
    {
        if (!IPAddress.TryParse(address, out IPAddress? ip))
        {
            return true;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        }

        byte[] b = ip.GetAddressBytes();
        return b[0] switch
        {
            10 => true,
            127 => true,
            169 when b[1] == 254 => true,
            172 when b[1] >= 16 && b[1] <= 31 => true,
            192 when b[1] == 168 => true,
            100 when b[1] >= 64 && b[1] <= 127 => true,
            _ => false,
        };
    }

    private void LoadCache()
    {
        try
        {
            string? json = AtomicStorage.LoadAtomic(_cachePath, out _);
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            Dictionary<string, GeoLocation>? data =
                JsonSerializer.Deserialize<Dictionary<string, GeoLocation>>(json);
            if (data is null)
            {
                return;
            }

            foreach ((string key, GeoLocation value) in data)
            {
                _cache[key] = value;
            }
        }
        catch (Exception)
        {
            // A corrupt cache is not worth failing over; it rebuilds itself.
        }
    }

    private void SaveCache()
    {
        try
        {
            string json = JsonSerializer.Serialize(_cache.ToDictionary(kv => kv.Key, kv => kv.Value));
            AtomicStorage.SaveAtomic(_cachePath, json);
        }
        catch (Exception)
        {
            // Non-critical.
        }
    }

    public void Dispose()
    {
        _running = false;
        SaveCache();
        _http.Dispose();
    }

    private sealed class IpApiResult
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("countryCode")] public string? CountryCode { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("query")] public string? Query { get; set; }
    }
}
