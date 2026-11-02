using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace Valve;

public partial class WhoisWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private string _currentIp = "";
    private double _lat;
    private double _lon;

    public WhoisWindow(string defaultTarget = "")
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(defaultTarget))
        {
            TargetBox.Text = CleanIp(defaultTarget);
            Loaded += (_, _) => DoLookup();
        }
    }

    private static string CleanIp(string endpoint)
    {
        string text = endpoint.Trim();
        int colon = text.IndexOf(':');
        if (colon > 0 && !text.Contains("::"))
        {
            return text.Substring(0, colon);
        }
        return text;
    }

    private void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DoLookup();
        }
    }

    private void Lookup_Click(object sender, RoutedEventArgs e) => DoLookup();

    private async void DoLookup()
    {
        string target = CleanIp(TargetBox.Text);
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusText.Text = "Please enter an IP address or hostname.";
            return;
        }

        StatusText.Text = $"Querying WHOIS & network intelligence for {target}...";
        DataGridInfo.Visibility = Visibility.Collapsed;
        BtnMap.IsEnabled = false;
        BtnSearch.IsEnabled = false;
        BtnCopy.IsEnabled = false;

        try
        {
            string url = $"http://ip-api.com/json/{target}?fields=status,message,country,regionName,city,zip,lat,lon,timezone,isp,org,as,query";
            string json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "fail")
            {
                string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "Lookup failed" : "Lookup failed";
                StatusText.Text = $"Lookup error: {msg}";
                return;
            }

            _currentIp = root.TryGetProperty("query", out var q) ? q.GetString() ?? target : target;
            string isp = root.TryGetProperty("isp", out var ispP) ? ispP.GetString() ?? "Unknown" : "Unknown";
            string org = root.TryGetProperty("org", out var orgP) ? orgP.GetString() ?? "" : "";
            string asn = root.TryGetProperty("as", out var asP) ? asP.GetString() ?? "Unknown" : "Unknown";
            string country = root.TryGetProperty("country", out var cP) ? cP.GetString() ?? "" : "";
            string region = root.TryGetProperty("regionName", out var rP) ? rP.GetString() ?? "" : "";
            string city = root.TryGetProperty("city", out var citP) ? citP.GetString() ?? "" : "";
            string timezone = root.TryGetProperty("timezone", out var tzP) ? tzP.GetString() ?? "-" : "-";
            _lat = root.TryGetProperty("lat", out var latP) ? latP.GetDouble() : 0;
            _lon = root.TryGetProperty("lon", out var lonP) ? lonP.GetDouble() : 0;

            TxtIp.Text = _currentIp;
            TxtIsp.Text = !string.IsNullOrEmpty(org) && org != isp ? $"{isp} ({org})" : isp;
            TxtAsn.Text = asn;
            TxtLocation.Text = $"{city}, {region}, {country}".Trim(',', ' ');
            TxtCoords.Text = $"{_lat:F4}, {_lon:F4}";
            TxtTimezone.Text = timezone;

            // Reverse DNS
            string rdns = "-";
            try
            {
                if (IPAddress.TryParse(_currentIp, out var ipAddr))
                {
                    var hostEntry = await Dns.GetHostEntryAsync(ipAddr);
                    rdns = hostEntry.HostName;
                }
            }
            catch { }
            TxtReverseDns.Text = rdns;

            StatusText.Text = "WHOIS lookup completed successfully.";
            DataGridInfo.Visibility = Visibility.Visible;
            BtnMap.IsEnabled = Math.Abs(_lat) > 0.001 || Math.Abs(_lon) > 0.001;
            BtnSearch.IsEnabled = true;
            BtnCopy.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Lookup exception: {ex.Message}";
        }
    }

    private void Map_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string url = $"https://www.google.com/maps/search/?api=1&query={_lat:F5},{_lon:F5}";
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private void WebSearch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string url = $"https://www.google.com/search?q=ip+{Uri.EscapeDataString(_currentIp)}";
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        string text = $"IP: {TxtIp.Text}\nISP: {TxtIsp.Text}\nASN: {TxtAsn.Text}\nLocation: {TxtLocation.Text}\nCoordinates: {TxtCoords.Text}\nTimezone: {TxtTimezone.Text}\nReverse DNS: {TxtReverseDns.Text}";
        Clipboard.SetText(text);
        MessageBox.Show(this, "WHOIS information copied to clipboard.", "Valve", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
