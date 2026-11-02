using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Valve.Core;

namespace Valve;

public partial class ProcessStatsWindow : Window
{
    private readonly ProcessRow _row;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;

    public ProcessStatsWindow(ProcessRow row, MainViewModel vm)
    {
        InitializeComponent();
        _row = row;
        _vm = vm;

        Title = $"Network Statistics — {(!string.IsNullOrEmpty(row.DisplayName) ? row.DisplayName : row.Name)} (PID {row.Pid})";
        AppNameText.Text = !string.IsNullOrEmpty(row.DisplayName) ? row.DisplayName : row.Name;
        PidText.Text = $"PID: {row.Pid}";
        AppPathText.Text = string.IsNullOrEmpty(row.Path) ? "No file path" : row.Path;
        if (row.Icon != null) AppIconImg.Source = row.Icon;

        RefreshDisplay();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (s, e) => RefreshDisplay();
        _timer.Start();

        Closed += (s, e) => _timer.Stop();
    }

    private void RefreshDisplay()
    {
        TotalInText.Text = _row.InTotalText;
        TotalOutText.Text = _row.OutTotalText;
        InRateText.Text = _row.InRateText;
        OutRateText.Text = _row.OutRateText;

        var matchingConns = _vm.Connections.Where(c => c.Pid == _row.Pid).ToList();
        ConnsGrid.ItemsSource = null;
        ConnsGrid.ItemsSource = matchingConns;
        ConnCountText.Text = $"{matchingConns.Count} active network socket{(matchingConns.Count == 1 ? "" : "s")}";

        long dlBps = (long)_row.DownloadLimitKbps * 1024 / 8;
        DlLimitSummaryText.Text = _row.IsDownloadLimitEnabled && _row.DownloadLimitKbps > 0
            ? $"{UnitConverter.FormatString(dlBps, false)} (Active)"
            : "No limit (unthrottled)";

        long ulBps = (long)_row.LimitKbps * 1024 / 8;
        UlLimitSummaryText.Text = _row.IsUploadLimitEnabled && _row.LimitKbps > 0
            ? $"{UnitConverter.FormatString(ulBps, false)} (Active)"
            : "No limit (unthrottled)";

        PrioritySummaryText.Text = _row.PriorityText;

        bool isAlive = _row.Pid > 0;
        StatusBadgeText.Text = isAlive ? "ACTIVE" : "IDLE";
        StatusBadge.Background = isAlive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 56, 43))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 44, 52));
    }

    private void OpenSetLimit_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SetLimitWindow(_row, _vm, isDownload: true) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            RefreshDisplay();
        }
    }

    private void EditDlLimit_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SetLimitWindow(_row, _vm, isDownload: true) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            RefreshDisplay();
        }
    }

    private void EditUlLimit_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SetLimitWindow(_row, _vm, isDownload: false) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            RefreshDisplay();
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Title = "Export Process Network Stats",
            FileName = $"{_row.Name}_NetworkStats_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            DefaultExt = ".csv"
        };
        if (sfd.ShowDialog() == true)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Process,PID,Path,TotalIn,TotalOut,CurrentInRate,CurrentOutRate");
                sb.AppendLine($"\"{_row.Name}\",{_row.Pid},\"{_row.Path}\",{_row.InTotal},{_row.OutTotal},\"{_row.InRateText}\",\"{_row.OutRateText}\"");
                sb.AppendLine();
                sb.AppendLine("Direction,Protocol,LocalEndpoint,RemoteEndpoint,Location,InRate,OutRate");
                var matchingConns = _vm.Connections.Where(c => c.Pid == _row.Pid).ToList();
                foreach (var c in matchingConns)
                {
                    sb.AppendLine($"\"{c.DirectionBadge}\",\"{c.Proto}\",\"{c.Local}\",\"{c.Remote}\",\"{c.Location}\",\"{c.InRateText}\",\"{c.OutRateText}\"");
                }
                File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show(this, "Process network stats successfully exported.", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to export CSV: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
