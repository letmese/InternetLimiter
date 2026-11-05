using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
    private string? _activeAppFilter = null;
    private List<HourlyBucket> _activeBuckets = new();
    private List<AppRankingSummary> _activeRankings = new();
    private DateTime _activeStart;
    private DateTime _activeEnd;
    private bool _isHourlyMode = true;
    private readonly System.Windows.Threading.DispatcherTimer _liveRefreshTimer;

    // Palette for horizontal ranking bars
    private static readonly Color[] Palette = new[]
    {
        Color.FromRgb(59, 130, 246),   // Blue
        Color.FromRgb(249, 115, 22),   // Orange
        Color.FromRgb(239, 68, 68),    // Red
        Color.FromRgb(16, 185, 129),   // Emerald
        Color.FromRgb(139, 92, 246),   // Violet
        Color.FromRgb(6, 182, 212),    // Cyan
        Color.FromRgb(236, 72, 153),   // Pink
        Color.FromRgb(234, 179, 8)     // Amber
    };

    public StatsManagerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        // Set custom date default strings
        CustomStartBox.Text = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        CustomEndBox.Text = DateTime.UtcNow.ToString("yyyy-MM-dd");

        _liveRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _liveRefreshTimer.Tick += (s, e) =>
        {
            if (CustomStartBox != null && CustomStartBox.IsFocused) return;
            if (CustomEndBox != null && CustomEndBox.IsFocused) return;
            RefreshAll();
        };

        Loaded += (s, e) =>
        {
            UpdateLayout();
            RenderTimeSeriesChart();
            _liveRefreshTimer.Start();
        };

        Closed += (s, e) =>
        {
            _liveRefreshTimer.Stop();
        };

        RefreshAll();
    }

    private void RefreshAll()
    {
        // 1. Refresh visual analytics charts
        RefreshAnalytics();

        // 2. Refresh raw grid data and DB overview
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
        RenderMiniHistoryChart(allTotals);
        StatusText.Text = "";

        // Footer DB Metrics
        FooterDbMetrics.Text = $"Database: traffic_stats.db ({Fmt.Bytes(totalBytes)}, {dayCount} days recorded)";
    }

    private void RefreshAnalytics()
    {
        if (_vm == null || ChartRangeTitle == null || Time24h == null || DlRankingContainer == null || UlRankingContainer == null)
        {
            return;
        }

        var sw = Stopwatch.StartNew();

        DateTime utcNow = DateTime.UtcNow;
        DateTime start;
        DateTime end = utcNow;
        bool isHourly = true;
        string rangeTitle;

        if (Time24h.IsChecked == true)
        {
            start = utcNow.AddHours(-24);
            isHourly = true;
            rangeTitle = $"{start.ToLocalTime():M/d/yyyy h:mm tt} — {end.ToLocalTime():M/d/yyyy h:mm tt}";
            if (CustomDateRow != null) CustomDateRow.Visibility = Visibility.Collapsed;
        }
        else if (TimeToday.IsChecked == true)
        {
            DateTime localNow = DateTime.Now;
            DateTime localStart = new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0);
            start = localStart.ToUniversalTime();
            isHourly = true;
            rangeTitle = $"Today: {localStart:M/d/yyyy 12:00 AM} — {localNow:h:mm tt}";
            if (CustomDateRow != null) CustomDateRow.Visibility = Visibility.Collapsed;
        }
        else if (Time7d.IsChecked == true)
        {
            start = utcNow.AddDays(-7);
            isHourly = false;
            rangeTitle = $"Last 7 Days: {start.ToLocalTime():M/d/yyyy} — {end.ToLocalTime():M/d/yyyy}";
            if (CustomDateRow != null) CustomDateRow.Visibility = Visibility.Collapsed;
        }
        else if (TimeMonth.IsChecked == true)
        {
            DateTime localNow = DateTime.Now;
            DateTime localStart = new DateTime(localNow.Year, localNow.Month, 1);
            start = localStart.ToUniversalTime();
            isHourly = false;
            rangeTitle = $"This Month: {localStart:MMMM yyyy}";
            if (CustomDateRow != null) CustomDateRow.Visibility = Visibility.Collapsed;
        }
        else if (TimeYear.IsChecked == true)
        {
            DateTime localNow = DateTime.Now;
            DateTime localStart = new DateTime(localNow.Year, 1, 1);
            start = localStart.ToUniversalTime();
            isHourly = false;
            rangeTitle = $"This Year: {localStart:yyyy}";
            if (CustomDateRow != null) CustomDateRow.Visibility = Visibility.Collapsed;
        }
        else if (TimeCustom.IsChecked == true)
        {
            if (CustomDateRow != null) CustomDateRow.Visibility = Visibility.Visible;
            if (TryParseDate(CustomStartBox.Text, out var dStart) && TryParseDate(CustomEndBox.Text, out var dEnd))
            {
                if (dEnd < dStart) (dStart, dEnd) = (dEnd, dStart);
                start = dStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                end = dEnd.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
                isHourly = (end - start).TotalDays <= 2;
                rangeTitle = $"{dStart:yyyy-MM-dd} → {dEnd:yyyy-MM-dd}";
            }
            else
            {
                start = utcNow.AddDays(-7);
                isHourly = false;
                rangeTitle = $"Custom: {start.ToLocalTime():M/d/yyyy} — {end.ToLocalTime():M/d/yyyy}";
            }
        }
        else // TimeAll
        {
            start = utcNow.AddYears(-5);
            isHourly = false;
            rangeTitle = "All Recorded Time";
            if (CustomDateRow != null) CustomDateRow.Visibility = Visibility.Collapsed;
        }

        _activeStart = start;
        _activeEnd = end;
        _isHourlyMode = isHourly;

        // Zone filter determination
        string zone = "internet";
        if (ZoneLan?.IsChecked == true) zone = "local";
        else if (ZoneAll?.IsChecked == true) zone = "all";

        string zonePrefix = zone == "local" ? "🏠 [Local LAN] " : (zone == "all" ? "⚡ [All Traffic] " : "🌐 [Internet] ");
        ChartRangeTitle.Text = zonePrefix + rangeTitle;

        // Active app filter badge
        if (!string.IsNullOrEmpty(_activeAppFilter))
        {
            ActiveFilterBadge.Visibility = Visibility.Visible;
            ActiveFilterText.Text = $"Filtered: {_activeAppFilter}";
        }
        else
        {
            ActiveFilterBadge.Visibility = Visibility.Collapsed;
        }

        // Direction filter
        int dirIndex = DirectionCombo?.SelectedIndex ?? 0;
        // 0 = All (In & Out), 1 = Download only, 2 = Upload only

        // Query time-series data from SQLite with Zone support
        long startEpoch = new DateTimeOffset(start).ToUnixTimeSeconds();
        long endEpoch = new DateTimeOffset(end).ToUnixTimeSeconds();
        var store = _vm.HistorySqliteStore;

        List<HourlyBucket> buckets;
        List<AppRankingSummary> rankings;

        if (isHourly)
        {
            buckets = store.QueryHourlySeries(startEpoch, endEpoch, _activeAppFilter, zone);
            rankings = store.QueryAppRankings(startEpoch: startEpoch, endEpoch: endEpoch, zone: zone);

            // Synthesize full 24-bucket grid if Last 24 Hours or Today
            buckets = FillHourlyGrid(start, end, buckets);
        }
        else
        {
            var fromDate = DateOnly.FromDateTime(start);
            var toDate = DateOnly.FromDateTime(end);
            buckets = store.QueryDailySeries(fromDate, toDate, _activeAppFilter, zone);
            rankings = store.QueryAppRankings(from: fromDate, to: toDate, zone: zone);

            // Fill daily grid
            buckets = FillDailyGrid(fromDate, toDate, buckets);
        }

        // Fallback: If SQLite was empty or freshly opened, populate from HistoryStore totals (for Internet zone)
        if (rankings.Count == 0 && zone != "local")
        {
            var fallback = _vm.GetAllAppTotals();
            rankings = fallback.Select(f => new AppRankingSummary
            {
                App = f.App,
                BytesIn = f.BytesIn,
                BytesOut = f.BytesOut
            }).ToList();

            if (buckets.All(b => b.BytesIn == 0 && b.BytesOut == 0) && fallback.Count > 0)
            {
                long totalIn = fallback.Sum(f => f.BytesIn);
                long totalOut = fallback.Sum(f => f.BytesOut);
                if (buckets.Count > 0)
                {
                    buckets[buckets.Count - 1].BytesIn = totalIn;
                    buckets[buckets.Count - 1].BytesOut = totalOut;
                }
            }
        }

        _activeBuckets = buckets;
        _activeRankings = rankings;

        long sumIn = buckets.Sum(b => b.BytesIn);
        long sumOut = buckets.Sum(b => b.BytesOut);
        long sumCombined = sumIn + sumOut;

        ChartDownloadTotalText.Text = $"Download ({Fmt.Bytes(sumIn)})";
        ChartUploadTotalText.Text = $"Upload ({Fmt.Bytes(sumOut)})";

        if (dirIndex == 1)
        {
            ChartUploadTotalText.Visibility = Visibility.Collapsed;
            ChartDownloadTotalText.Visibility = Visibility.Visible;
            ChartCombinedTotalText.Text = $"Download: {Fmt.Bytes(sumIn)}";
        }
        else if (dirIndex == 2)
        {
            ChartDownloadTotalText.Visibility = Visibility.Collapsed;
            ChartUploadTotalText.Visibility = Visibility.Visible;
            ChartCombinedTotalText.Text = $"Upload: {Fmt.Bytes(sumOut)}";
        }
        else
        {
            ChartDownloadTotalText.Visibility = Visibility.Visible;
            ChartUploadTotalText.Visibility = Visibility.Visible;
            ChartCombinedTotalText.Text = $"Total: {Fmt.Bytes(sumCombined)}";
        }

        DlRankingTotalBadge.Text = $"({Fmt.Bytes(sumIn)})";
        UlRankingTotalBadge.Text = $"({Fmt.Bytes(sumOut)})";

        // Render upper time-series canvas
        RenderTimeSeriesChart();

        // Render lower breakdown ranking bars
        RenderBreakdownRankings(rankings, sumIn, sumOut);

        sw.Stop();
        string zoneLabel = zone == "local" ? "LAN" : (zone == "all" ? "All" : "Internet");
        FooterTelemetryMetrics.Text = $"Status: Active | Zone: {zoneLabel} | Active Apps: {rankings.Count} | Total Traffic: {Fmt.Bytes(sumCombined)} | ⬇️ In: {Fmt.Bytes(sumIn)} | ⬆️ Out: {Fmt.Bytes(sumOut)} | Query Time: {sw.ElapsedMilliseconds} ms";
    }

    private List<HourlyBucket> FillHourlyGrid(DateTime start, DateTime end, List<HourlyBucket> existing)
    {
        var map = existing.ToDictionary(b => b.Epoch);
        var result = new List<HourlyBucket>();

        // Align start to the hour boundary
        DateTime cur = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
        while (cur <= end)
        {
            long ep = new DateTimeOffset(cur).ToUnixTimeSeconds();
            if (map.TryGetValue(ep, out var found))
            {
                result.Add(found);
            }
            else
            {
                result.Add(new HourlyBucket
                {
                    Epoch = ep,
                    LabelTime = cur.ToLocalTime(),
                    BytesIn = 0,
                    BytesOut = 0
                });
            }
            cur = cur.AddHours(1);
        }
        return result;
    }

    private List<HourlyBucket> FillDailyGrid(DateOnly from, DateOnly to, List<HourlyBucket> existing)
    {
        var map = existing.ToDictionary(b => DateOnly.FromDateTime(b.LabelTime));
        var result = new List<HourlyBucket>();

        DateOnly cur = from;
        while (cur <= to)
        {
            if (map.TryGetValue(cur, out var found))
            {
                result.Add(found);
            }
            else
            {
                DateTime dt = cur.ToDateTime(TimeOnly.MinValue);
                result.Add(new HourlyBucket
                {
                    Epoch = new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds(),
                    LabelTime = dt,
                    BytesIn = 0,
                    BytesOut = 0
                });
            }
            cur = cur.AddDays(1);
        }
        return result;
    }

    private void RenderTimeSeriesChart()
    {
        TimeSeriesCanvas.Children.Clear();
        if (_activeBuckets == null || _activeBuckets.Count == 0) return;

        double canvasW = TimeSeriesCanvas.ActualWidth > 80 ? TimeSeriesCanvas.ActualWidth : (this.Width > 150 ? this.Width - 60 : 1050);
        double canvasH = TimeSeriesCanvas.ActualHeight > 60 ? TimeSeriesCanvas.ActualHeight : 260;

        double leftMargin = 75;
        double rightMargin = 20;
        double topMargin = 25;
        double bottomMargin = 32;

        double chartW = canvasW - leftMargin - rightMargin;
        double chartH = canvasH - topMargin - bottomMargin;
        if (chartW <= 10 || chartH <= 10) return;

        int dirIndex = DirectionCombo?.SelectedIndex ?? 0;
        bool drawIn = dirIndex == 0 || dirIndex == 1;
        bool drawOut = dirIndex == 0 || dirIndex == 2;

        // Find max bytes for Y-axis scale
        long maxBytes = 1024 * 1024; // 1 MB default floor
        foreach (var b in _activeBuckets)
        {
            if (drawIn && b.BytesIn > maxBytes) maxBytes = b.BytesIn;
            if (drawOut && b.BytesOut > maxBytes) maxBytes = b.BytesOut;
        }

        // Draw horizontal grid lines & Y-axis labels (4 steps: 0, 33%, 66%, 100%)
        var gridLineBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
        var axisTextBrush = new SolidColorBrush(Color.FromRgb(110, 118, 129));

        for (int step = 0; step <= 4; step++)
        {
            double ratio = step / 4.0;
            double y = topMargin + chartH - (ratio * chartH);
            long val = (long)(ratio * maxBytes);

            // Horizontal line
            var line = new Line
            {
                X1 = leftMargin,
                X2 = leftMargin + chartW,
                Y1 = y,
                Y2 = y,
                Stroke = gridLineBrush,
                StrokeThickness = 1,
                StrokeDashArray = (step > 0 && step < 4) ? new DoubleCollection { 3, 3 } : null
            };
            TimeSeriesCanvas.Children.Add(line);

            // Y-axis label text
            var tb = new TextBlock
            {
                Text = Fmt.Bytes(val),
                Foreground = axisTextBrush,
                FontSize = 10,
                Width = leftMargin - 10,
                TextAlignment = TextAlignment.Right
            };
            Canvas.SetLeft(tb, 0);
            Canvas.SetTop(tb, y - 7);
            TimeSeriesCanvas.Children.Add(tb);
        }

        // Render Columns
        int count = _activeBuckets.Count;
        double slotWidth = chartW / count;
        double barWidth = Math.Max(2, Math.Min(22, (slotWidth / 2) - 3));

        // Colors
        var inBrush = new LinearGradientBrush(
            Color.FromRgb(16, 185, 129),  // #10B981 Emerald
            Color.FromRgb(5, 150, 105),   // #059669 Darker Emerald
            new Point(0, 0), new Point(0, 1));

        var outBrush = new LinearGradientBrush(
            Color.FromRgb(244, 63, 94),   // #F43F5E Coral Rose
            Color.FromRgb(190, 18, 60),   // #BE123C Crimson
            new Point(0, 0), new Point(0, 1));

        int labelInterval = Math.Max(1, count / 7);

        for (int i = 0; i < count; i++)
        {
            var bucket = _activeBuckets[i];
            double slotCenterX = leftMargin + (i * slotWidth) + (slotWidth / 2);

            double inH = (bucket.BytesIn / (double)maxBytes) * chartH;
            double outH = (bucket.BytesOut / (double)maxBytes) * chartH;

            double inBarW = (drawIn && !drawOut) ? barWidth * 1.5 : barWidth;
            double outBarW = (!drawIn && drawOut) ? barWidth * 1.5 : barWidth;

            // Inbound column
            if (drawIn && (inH > 0 || bucket.BytesIn > 0))
            {
                double drawH = Math.Max(2, inH);
                var inRect = new Rectangle
                {
                    Width = inBarW,
                    Height = drawH,
                    Fill = inBrush,
                    RadiusX = 2,
                    RadiusY = 2,
                    Tag = bucket
                };
                double inX = (drawIn && !drawOut) ? (slotCenterX - inBarW / 2) : (slotCenterX - barWidth - 1);
                Canvas.SetLeft(inRect, inX);
                Canvas.SetTop(inRect, topMargin + chartH - drawH);
                TimeSeriesCanvas.Children.Add(inRect);
            }

            // Outbound column
            if (drawOut && (outH > 0 || bucket.BytesOut > 0))
            {
                double drawH = Math.Max(2, outH);
                var outRect = new Rectangle
                {
                    Width = outBarW,
                    Height = drawH,
                    Fill = outBrush,
                    RadiusX = 2,
                    RadiusY = 2,
                    Tag = bucket
                };
                double outX = (!drawIn && drawOut) ? (slotCenterX - outBarW / 2) : (slotCenterX + 1);
                Canvas.SetLeft(outRect, outX);
                Canvas.SetTop(outRect, topMargin + chartH - drawH);
                TimeSeriesCanvas.Children.Add(outRect);
            }

            // X-axis label
            if (i % labelInterval == 0 || i == count - 1)
            {
                string xLabel = _isHourlyMode
                    ? bucket.LabelTime.ToString("h:mm tt")
                    : bucket.LabelTime.ToString("MMM d");

                var xText = new TextBlock
                {
                    Text = xLabel,
                    Foreground = axisTextBrush,
                    FontSize = 10,
                    TextAlignment = TextAlignment.Center,
                    Width = 65
                };
                Canvas.SetLeft(xText, slotCenterX - 32.5);
                Canvas.SetTop(xText, topMargin + chartH + 8);
                TimeSeriesCanvas.Children.Add(xText);
            }
        }
    }

    private void RenderBreakdownRankings(List<AppRankingSummary> rankings, long totalIn, long totalOut)
    {
        DlRankingContainer.Children.Clear();
        UlRankingContainer.Children.Clear();

        if (rankings == null || rankings.Count == 0) return;

        // 1. Download Rankings
        var topDl = rankings.OrderByDescending(r => r.BytesIn).Where(r => r.BytesIn > 0).ToList();
        long maxDl = topDl.FirstOrDefault()?.BytesIn ?? 1;
        DlScaleMax.Text = Fmt.Bytes(maxDl);
        DlScaleMid.Text = Fmt.Bytes(maxDl / 2);

        int dlLimit = Math.Min(7, topDl.Count);
        long dlRestBytes = 0;

        for (int i = 0; i < topDl.Count; i++)
        {
            var item = topDl[i];
            if (i < dlLimit)
            {
                Color barColor = Palette[i % Palette.Length];
                double ratio = maxDl > 0 ? (double)item.BytesIn / maxDl : 0;
                double pct = totalIn > 0 ? ((double)item.BytesIn / totalIn) * 100 : 0;

                var row = CreateRankingRow(item.App, Fmt.Bytes(item.BytesIn), pct, ratio, barColor);
                DlRankingContainer.Children.Add(row);
            }
            else
            {
                dlRestBytes += item.BytesIn;
            }
        }

        if (dlRestBytes > 0)
        {
            double ratio = maxDl > 0 ? (double)dlRestBytes / maxDl : 0;
            double pct = totalIn > 0 ? ((double)dlRestBytes / totalIn) * 100 : 0;
            var restRow = CreateRankingRow("Rest of Apps", Fmt.Bytes(dlRestBytes), pct, ratio, Color.FromRgb(100, 116, 139), isRest: true);
            DlRankingContainer.Children.Add(restRow);
        }

        // 2. Upload Rankings
        var topUl = rankings.OrderByDescending(r => r.BytesOut).Where(r => r.BytesOut > 0).ToList();
        long maxUl = topUl.FirstOrDefault()?.BytesOut ?? 1;
        UlScaleMax.Text = Fmt.Bytes(maxUl);
        UlScaleMid.Text = Fmt.Bytes(maxUl / 2);

        int ulLimit = Math.Min(7, topUl.Count);
        long ulRestBytes = 0;

        for (int i = 0; i < topUl.Count; i++)
        {
            var item = topUl[i];
            if (i < ulLimit)
            {
                Color barColor = Palette[(i + 2) % Palette.Length];
                double ratio = maxUl > 0 ? (double)item.BytesOut / maxUl : 0;
                double pct = totalOut > 0 ? ((double)item.BytesOut / totalOut) * 100 : 0;

                var row = CreateRankingRow(item.App, Fmt.Bytes(item.BytesOut), pct, ratio, barColor);
                UlRankingContainer.Children.Add(row);
            }
            else
            {
                ulRestBytes += item.BytesOut;
            }
        }

        if (ulRestBytes > 0)
        {
            double ratio = maxUl > 0 ? (double)ulRestBytes / maxUl : 0;
            double pct = totalOut > 0 ? ((double)ulRestBytes / totalOut) * 100 : 0;
            var restRow = CreateRankingRow("Rest of Apps", Fmt.Bytes(ulRestBytes), pct, ratio, Color.FromRgb(100, 116, 139), isRest: true);
            UlRankingContainer.Children.Add(restRow);
        }
    }

    private Border CreateRankingRow(string appName, string byteText, double percent, double barRatio, Color barColor, bool isRest = false)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 2, 0, 2),
            Cursor = isRest ? Cursors.Arrow : Cursors.Hand,
            ToolTip = isRest ? null : $"Click to filter analytics timeline to '{appName}'"
        };

        if (!isRest)
        {
            border.MouseEnter += (s, e) => border.Background = new SolidColorBrush(Color.FromArgb(45, 56, 189, 248));
            border.MouseLeave += (s, e) => border.Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
            border.MouseDown += (s, e) =>
            {
                _activeAppFilter = appName;
                RefreshAnalytics();
            };
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: App Name + Bytes + Percentage
        var topDock = new DockPanel { LastChildFill = false };

        var nameText = new TextBlock
        {
            Text = appName,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = isRest ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        DockPanel.SetDock(nameText, Dock.Left);
        topDock.Children.Add(nameText);

        var rightStack = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(rightStack, Dock.Right);

        var volText = new TextBlock
        {
            Text = byteText,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
            Margin = new Thickness(0, 0, 8, 0)
        };
        rightStack.Children.Add(volText);

        var pctText = new TextBlock
        {
            Text = $"{percent:0.0}%",
            FontSize = 10,
            Foreground = new SolidColorBrush(barColor),
            FontWeight = FontWeights.Bold
        };
        rightStack.Children.Add(pctText);
        topDock.Children.Add(rightStack);

        Grid.SetRow(topDock, 0);
        grid.Children.Add(topDock);

        // Row 1: Horizontal Bar Track
        var track = new Border
        {
            Height = 6,
            Background = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var fillBar = new Border
        {
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(3),
            Background = new LinearGradientBrush(
                barColor,
                Color.FromArgb(200, barColor.R, barColor.G, barColor.B),
                new Point(0, 0), new Point(1, 0))
        };

        track.SizeChanged += (s, e) =>
        {
            fillBar.Width = Math.Max(3, track.ActualWidth * Math.Min(1.0, barRatio));
        };

        track.Child = fillBar;
        Grid.SetRow(track, 1);
        grid.Children.Add(track);

        border.Child = grid;
        return border;
    }

    private void TimeSeriesCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_activeBuckets == null || _activeBuckets.Count == 0) return;

        double canvasW = TimeSeriesCanvas.ActualWidth;
        double canvasH = TimeSeriesCanvas.ActualHeight;
        double leftMargin = 75;
        double rightMargin = 20;
        double chartW = canvasW - leftMargin - rightMargin;

        Point p = e.GetPosition(TimeSeriesCanvas);
        if (p.X < leftMargin || p.X > leftMargin + chartW)
        {
            ChartTooltip.Visibility = Visibility.Collapsed;
            return;
        }

        double slotW = chartW / _activeBuckets.Count;
        int idx = (int)((p.X - leftMargin) / slotW);
        if (idx >= 0 && idx < _activeBuckets.Count)
        {
            var b = _activeBuckets[idx];
            TooltipTime.Text = _isHourlyMode
                ? b.LabelTime.ToString("yyyy-MM-dd h:mm tt")
                : b.LabelTime.ToString("yyyy-MM-dd");

            TooltipIn.Text = Fmt.Bytes(b.BytesIn);
            TooltipOut.Text = Fmt.Bytes(b.BytesOut);
            TooltipTotal.Text = Fmt.Bytes(b.BytesIn + b.BytesOut);

            double tipX = p.X + 12;
            if (tipX + 150 > canvasW) tipX = p.X - 160;
            double tipY = Math.Max(10, p.Y - 40);

            Canvas.SetLeft(ChartTooltip, tipX);
            Canvas.SetTop(ChartTooltip, tipY);
            ChartTooltip.Visibility = Visibility.Visible;
        }
        else
        {
            ChartTooltip.Visibility = Visibility.Collapsed;
        }
    }

    private void TimeSeriesCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        ChartTooltip.Visibility = Visibility.Collapsed;
    }

    private void TimeSeriesCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderTimeSeriesChart();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        RefreshAnalytics();
    }

    private void ApplyCustomRange_Click(object sender, RoutedEventArgs e)
    {
        RefreshAnalytics();
    }

    private void ClearAppFilter_Click(object sender, RoutedEventArgs e)
    {
        _activeAppFilter = null;
        RefreshAnalytics();
    }

    private void ToggleView_Click(object sender, RoutedEventArgs e)
    {
        if (ChartsView.Visibility == Visibility.Visible)
        {
            ChartsView.Visibility = Visibility.Collapsed;
            GridView.Visibility = Visibility.Visible;
            ViewToggleBtn.Content = "📊 Visual Charts";
        }
        else
        {
            ChartsView.Visibility = Visibility.Visible;
            GridView.Visibility = Visibility.Collapsed;
            ViewToggleBtn.Content = "📋 Data Grid";
            RenderTimeSeriesChart();
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sfd = new SaveFileDialog
            {
                Title = "Export Traffic Statistics",
                Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
                FileName = $"InternetLimiter_Traffic_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (sfd.ShowDialog() == true)
            {
                using var sw = new StreamWriter(sfd.FileName);
                sw.WriteLine("Application,InboundBytes,InboundFormatted,OutboundBytes,OutboundFormatted,TotalBytes,TotalFormatted");
                foreach (var r in _activeRankings.OrderByDescending(x => x.BytesIn + x.BytesOut))
                {
                    sw.WriteLine($"\"{r.App}\",{r.BytesIn},\"{Fmt.Bytes(r.BytesIn)}\",{r.BytesOut},\"{Fmt.Bytes(r.BytesOut)}\",{r.BytesIn + r.BytesOut},\"{Fmt.Bytes(r.BytesIn + r.BytesOut)}\"");
                }
                MessageBox.Show($"Successfully exported traffic statistics to:\n{sfd.FileName}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RenderMiniHistoryChart(IReadOnlyList<DailyUsage> data)
    {
        HistoryChartCanvas.Children.Clear();
        if (data == null || data.Count == 0) return;

        double width = HistoryChartCanvas.ActualWidth > 50 ? HistoryChartCanvas.ActualWidth : 800;
        double height = HistoryChartCanvas.ActualHeight > 30 ? HistoryChartCanvas.ActualHeight : 65;

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
            var inRect = new Rectangle
            {
                Width = barWidth / 2,
                Height = Math.Max(2, inH),
                Fill = new SolidColorBrush(Color.FromRgb(46, 160, 67)),
                ToolTip = $"{item.App}: In {Fmt.Bytes(item.BytesIn)}"
            };
            Canvas.SetLeft(inRect, x);
            Canvas.SetTop(inRect, height - inRect.Height);
            HistoryChartCanvas.Children.Add(inRect);

            // Outbound bar
            var outRect = new Rectangle
            {
                Width = barWidth / 2,
                Height = Math.Max(2, outH),
                Fill = new SolidColorBrush(Color.FromRgb(218, 54, 51)),
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
            if (needle.Length == 0 || row.App.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                view.Add(row);
            }
        }

        string? selectedApp = (StatsGrid.SelectedItem as StatsManagerRow)?.App;
        StatsGrid.ItemsSource = view;
        if (selectedApp != null)
        {
            var match = view.FirstOrDefault(r => string.Equals(r.App, selectedApp, StringComparison.OrdinalIgnoreCase));
            if (match != null) StatsGrid.SelectedItem = match;
        }
    }

    private void AppSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void StatsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = false;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAll();

    private bool TryParseDate(string text, out DateOnly date)
        => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private void DeleteRange_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseDate(FromBox.Text, out DateOnly start) || !TryParseDate(ToBox.Text, out DateOnly end))
        {
            StatusText.Text = "dates must look like 2026-01-01";
            return;
        }

        if (end < start)
        {
            (start, end) = (end, start);
        }

        string confirm = $"Delete recorded traffic from {start:yyyy-MM-dd} to {end:yyyy-MM-dd}?";
        if (MessageBox.Show(confirm, "Internet Limiter — Stats Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
        if (MessageBox.Show($"Delete all recorded traffic for: {names}?", "Internet Limiter — Stats Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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

        if (MessageBox.Show($"Merge {sources.Count} app(s) into '{target}'?", "Internet Limiter — Stats Manager", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _vm.MergeAppHistory(target, sources);
        RefreshAll();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick a history folder to import" };
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
