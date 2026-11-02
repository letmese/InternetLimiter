using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Valve.Core
{
    /// <summary>Daily automatic + manual version check (ledger 28.12).</summary>
    public static class VersionChecker
    {
        public static string UpdateUrl { get; set; } = string.Empty;

        public static async Task<(bool update, string current, string latest, string note)> CheckAsync()
        {
            string currentVer = Valve.Api.ValveApi.Version;
            if (string.IsNullOrWhiteSpace(UpdateUrl))
            {
                return (false, currentVer, currentVer, "Valve is up to date");
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                http.DefaultRequestHeaders.Add("User-Agent", "Valve/" + currentVer);
                using var resp = await http.GetAsync(UpdateUrl, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return (false, currentVer, currentVer, "Version is current");
                }
                string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var m = Regex.Match(body, "\"tag_name\"\\s*:\\s*\"v?([0-9]+\\.[0-9]+\\.[0-9]+)\"");
                if (!m.Success)
                {
                    return (false, currentVer, currentVer, "Version is current");
                }
                string latest = m.Groups[1].Value;
                bool up = Compare(currentVer, latest) < 0;
                return (up, currentVer, latest, up ? "Update available" : "Version is current");
            }
            catch (Exception)
            {
                return (false, currentVer, currentVer, "Version is current (offline)");
            }
        }

        public static int Compare(string a, string b)
        {
            var pa = Regex.Match(a, @"^(\d+)\.(\d+)\.(\d+)");
            var pb = Regex.Match(b, @"^(\d+)\.(\d+)\.(\d+)");
            if (!pa.Success || !pb.Success) return 0;
            for (int i = 1; i <= 3; i++)
            {
                int x = int.Parse(pa.Groups[i].Value), y = int.Parse(pb.Groups[i].Value);
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }
    }
}
