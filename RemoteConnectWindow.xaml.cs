using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Valve.Core;

namespace Valve
{
    /// <summary>Remote Connect dialog (ledger 15.01-15.09): host list, port 4045 default,
    /// optional user field (blank = current user), shared-token auth, per-connection control.</summary>
    public partial class RemoteConnectWindow : Window
    {
        private static readonly string HostsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Valve", "remote_hosts.txt");
        private readonly SettingsStore _store = new();
        private readonly Settings _settings;

        public RemoteConnectWindow()
        {
            InitializeComponent();
            _settings = _store.Load();
            LoadHosts();
            PortBox.Text = _settings.RemotePort > 0 ? _settings.RemotePort.ToString() : "4045";
            TokenBox.Text = _settings.RemoteToken;
            StartServerBox.IsChecked = _settings.RemoteListen;
            if (_settings.RemoteListen) TryStartServer();
        }

        private void LoadHosts()
        {
            try
            {
                if (!File.Exists(HostsFile)) return;
                foreach (var line in File.ReadAllLines(HostsFile))
                    if (!string.IsNullOrWhiteSpace(line)) HostList.Items.Add(line);
            }
            catch { }
        }

        private void SaveHosts()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HostsFile)!);
                File.WriteAllLines(HostsFile, new List<string>(HostList.Items.Cast<object>().Select(i => i.ToString()!)));
            }
            catch { }
        }

        private void HostList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HostList.SelectedItem is string s)
            {
                var parts = s.Split('|');
                if (parts.Length >= 3) { HostBox.Text = parts[0]; PortBox.Text = parts[1]; TokenBox.Text = parts[2]; }
            }
        }

        private void SaveHost_Click(object sender, RoutedEventArgs e)
        {
            string host = HostBox.Text.Trim();
            if (host.Length == 0) return;
            string entry = $"{host}|{PortBox.Text.Trim()}|{TokenBox.Text.Trim()}|{UserBox.Text.Trim()}";
            foreach (object item in HostList.Items)
                if (item.ToString() == entry) return;
            HostList.Items.Add(entry);
            SaveHosts();
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            string host = HostBox.Text.Trim();
            if (host.Length == 0) { ResultBox.Text = "no host"; return; }
            if (!int.TryParse(PortBox.Text.Trim(), out int port) || port < 1 || port > 65535) { ResultBox.Text = "bad port"; return; }
            string token = TokenBox.Text.Trim();
            if (token.Length == 0) { ResultBox.Text = "token required"; return; }
            try
            {
                string resp = RemoteAdmin.SendCommand(host, port, token, "SNAPSHOT");
                ResultBox.Text = Pretty(resp);
            }
            catch (Exception ex) { ResultBox.Text = "connect failed: " + ex.Message; }
        }

        private void ServerToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (StartServerBox == null) return;
            string token = TokenBox.Text.Trim();
            if (StartServerBox.IsChecked == true && string.IsNullOrWhiteSpace(token))
            {
                StartServerBox.IsChecked = false;
                ResultBox.Text = "listen failed: token required";
                return;
            }
            _settings.RemoteListen = StartServerBox.IsChecked == true;
            _settings.RemotePort = int.TryParse(PortBox.Text.Trim(), out int p) ? p : 4045;
            _settings.RemoteToken = token;
            _store.Save(_settings);
            if (_settings.RemoteListen) TryStartServer(); else RemoteAdmin.StopServer();
        }

        private void TryStartServer()
        {
            if (string.IsNullOrWhiteSpace(_settings.RemoteToken))
            {
                ResultBox.Text = "listen failed: token required";
                if (StartServerBox != null) StartServerBox.IsChecked = false;
                return;
            }
            if (RemoteAdmin.StartServer(_settings.RemotePort > 0 ? _settings.RemotePort : 4045, _settings.RemoteToken, out string err))
                ResultBox.Text = $"listening on port {_settings.RemotePort}";
            else
                ResultBox.Text = "listen failed: " + err;
        }

        private static string Pretty(string json)
        {
            try { return System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Deserialize<object>(json), System.Text.Json.JsonSerializerOptions.Default); }
            catch { return json; }
        }
    }
}
