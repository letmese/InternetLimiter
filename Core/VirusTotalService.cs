using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Valve.Core;

/// <summary>Real VirusTotal API client (ledger 13.x). Computes a SHA-256 and queries the
/// public API when a user-supplied key is configured. Falls back to browser lookup otherwise.</summary>
public static class VirusTotalService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        BaseAddress = new Uri("https://www.virustotal.com/api/v3/"),
    };

    public sealed record Report(int Malicious, int Harmless, int Undetected, int Total, string ScanDate)
    {
        public static readonly Report Empty = new(0, 0, 0, 0, "");
    }

    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>Returns null when the file hash is unknown to VirusTotal.</summary>
    public static async Task<Report?> ScanAsync(string sha256, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "files/" + sha256);
        request.Headers.TryAddWithoutValidation("x-apikey", apiKey);
        using HttpResponseMessage response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null; // 404 unknown hash, 401 bad key, 429 rate limited - all surface as "no report"
        }

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");
        JsonElement stats = attrs.GetProperty("last_analysis_stats");
        string date = attrs.TryGetProperty("last_analysis_date", out JsonElement d)
            ? DateTimeOffset.FromUnixTimeSeconds(d.GetInt64()).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "";

        return new Report(
            stats.GetProperty("malicious").GetInt32(),
            stats.GetProperty("harmless").GetInt32(),
            stats.GetProperty("undetected").GetInt32(),
            stats.GetProperty("suspicious").GetInt32() + stats.GetProperty("malicious").GetInt32() +
            stats.GetProperty("harmless").GetInt32() + stats.GetProperty("undetected").GetInt32(),
            date);
    }
}
