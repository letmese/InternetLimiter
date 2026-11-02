using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Valve.Core;

namespace Valve;

public sealed class StatsManagerRow
{
    public StatsManagerRow(DailyUsage usage)
    {
        App = usage.App;
        Download = usage.BytesIn;
        Upload = usage.BytesOut;
    }

    public string App { get; }
    public long Download { get; }
    public long Upload { get; }
    public string DownloadText => Fmt.Bytes(Download);
    public string UploadText => Fmt.Bytes(Upload);
    public string TotalText => Fmt.Bytes(Download + Upload);
}

public partial class StatsManagerWindow : Window
{
    private readonly MainViewModel _vm;
    private List<StatsManagerRow> _all = new();

    public StatsManagerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        RefreshAll();
    }

    private void RefreshAll()
    {
        (int dayCount, long totalBytes) = _vm.GetHistoryStats();
        DayCountText.Text = dayCount.ToString("N0");
        SizeText.Text = Fmt.Bytes(totalBytes);

        (System.DateOnly? first, System.DateOnly? last) = _vm.GetHistoryDateRange();
        SpanText.Text = first is null || last is null
            ? "no data"
            : $"{first:yyyy-MM-dd} → {last:yyyy-MM-dd}";

        var allTotals = _vm.GetAllAppTotals();
        var rows = new List<StatsManagerRow>();
        foreach (DailyUsage usage in allTotals)
        {
            rows.Add(new StatsManagerRow(usage));
        }

        _all = rows;
        ApplyFilter();
        RenderHistoryChart(allTotals);
        StatusText.Text = "";
    }

    private void RenderHistoryChart(IReadOnlyList<DailyUsage> data)
    {
        HistoryChartCanvas.Children.Clear();
        if (data == null || data.Count == 0) return;

        double width = HistoryChartCanvas.ActualWidth > 50 ? HistoryChartCanvas.ActualWidth : 800;
        double height = HistoryChartCanvas.ActualHeight > 30 ? HistoryChartCanvas.ActualHeight : 80;

        long maxBytes = 1;
        foreach (var d in data)
        {
            if (d.BytesIn > maxBytes) maxBytes = d.BytesIn;
            if (d.BytesOut > maxBytes) maxBytes = d.BytesOut;
        }

        int count = Math.Min(data.Count, 30);
        double barWidth = Math.Max(4, (width / count) - 4);

        for (int i = 0; i < count; i++)
        {
            var item = data[i];
            double inH = Math.Min(height - 4, (item.BytesIn / (double)maxBytes) * (height - 10));
            double outH = Math.Min(height - 4, (item.BytesOut / (double)maxBytes) * (height - 10));

            double x = i * (width / count) + 2;

            // Inbound bar
            var inRect = new System.Windows.Shapes.Rectangle
            {
                Width = barWidth / 2,
                Height = Math.Max(2, inH),
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 160, 67)),
                ToolTip = $"{item.App}: In {Fmt.Bytes(item.BytesIn)}"
            };
            Canvas.SetLeft(inRect, x);
            Canvas.SetTop(inRect, height - inRect.Height);
            HistoryChartCanvas.Children.Add(inRect);

            // Outbound bar
            var outRect = new System.Windows.Shapes.Rectangle
            {
                Width = barWidth / 2,
                Height = Math.Max(2, outH),
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51)),
                ToolTip = $"{item.App}: Out {Fmt.Bytes(item.BytesOut)}"
            };
            Canvas.SetLeft(outRect, x + (barWidth / 2));
            Canvas.SetTop(outRect, height - outRect.Height);
            HistoryChartCanvas.Children.Add(outRect);
        }
    }

    private void ApplyFilter()
    {
        string needle = AppSearch.Text.Trim().ToLowerInvariant();
        var view = new List<StatsManagerRow>();
        foreach (StatsManagerRow row in _all)
        {
            if (needle.Length == 0 || row.App.Contains(needle, System.StringComparison.OrdinalIgnoreCase))
            {
                view.Add(row);
            }
        }

        StatsGrid.ItemsSource = view;
    }

    private void AppSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void StatsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // Built-in sorting on our read-only columns is enough; keep default behavior.
        e.Handled = false;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAll();

    private bool TryParseDate(string text, out System.DateOnly date)
        => System.DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date);

    private void DeleteRange_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseDate(FromBox.Text, out System.DateOnly start) || !TryParseDate(ToBox.Text, out System.DateOnly end))
        {
            StatusText.Text = "dates must look like 2026-01-01";
            return;
        }

        if (end < start)
        {
            (start, end) = (end, start);
        }

        string confirm = $"Delete recorded traffic from {start:yyyy-MM-dd} to {end:yyyy-MM-dd}?";
        if (MessageBox.Show(confirm, "Valve — Stats Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _vm.ClearHistoryRange(start, end);
        RefreshAll();
    }

    private void DeleteApp_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedApps();
        if (selected.Count == 0)
        {
            StatusText.Text = "select at least one app";
            return;
        }

        string names = string.Join(", ", selected);
        if (MessageBox.Show($"Delete all recorded traffic for: {names}?", "Valve — Stats Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (string app in selected)
        {
            _vm.DeleteAppHistory(app);
        }

        RefreshAll();
    }

    private void Repair_Click(object sender, RoutedEventArgs e)
    {
        _vm.RepairHistory();
        StatusText.Text = "integrity check + repair done";
        RefreshAll();
    }

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        // Rewrites every day file through the atomic writer, removing .bak cruft.
        int written = _vm.CompactHistory();
        StatusText.Text = $"rewrote {written} day files";
        RefreshAll();
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        string target = MergeTarget.Text.Trim().ToLowerInvariant();
        var sources = SelectedApps();
        if (target.Length == 0 || sources.Count == 0)
        {
            StatusText.Text = "enter a target name and select source apps";
            return;
        }

        sources.Remove(target);
        if (sources.Count == 0)
        {
            StatusText.Text = "target equals selection; nothing to merge";
            return;
        }

        if (MessageBox.Show($"Merge {sources.Count} app(s) into '{target}'?", "Valve — Stats Manager", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _vm.MergeAppHistory(target, sources);
        RefreshAll();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick a Valve history folder to import" };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        int imported = _vm.ImportHistory(dlg.FolderName);
        StatusText.Text = imported == 0 ? "no valid day files found" : $"imported {imported} day file(s)";
        RefreshAll();
    }

    private List<string> SelectedApps()
    {
        var list = new List<string>();
        foreach (StatsManagerRow row in StatsGrid.SelectedItems)
        {
            list.Add(row.App);
        }

        return list;
    }
}
