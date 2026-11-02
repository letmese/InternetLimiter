using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Valve.Core;

namespace Valve;

/// <summary>One overflowing application row in the quota alert window.</summary>
public sealed class QuotaAlertRow : Observable
{
    private static readonly Brush OverBrush = Freeze(Color.FromRgb(0xE2, 0x60, 0x4F));
    private static readonly Brush NearBrush = Freeze(Color.FromRgb(0xE8, 0xB0, 0x4B));

    public QuotaAlertRow(RuleRow row)
    {
        Row = row;
        IsOverflow = row.OverQuota;
    }

    public RuleRow Row { get; }
    public string App => Row.App;
    public string QuotaText => Row.QuotaText;
    public bool IsOverflow { get; }
    public string StatusText => IsOverflow ? "OVER" : "near";
    public Brush StatusBrush => IsOverflow ? OverBrush : NearBrush;

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Live quota status window (7.04.a notification view, 7.07.a-d actions and
/// pagination, 7.08 reopenable "Show Quota Status"). Lists every process with a
/// configured quota and one-click Kill / Ignore / Reset actions.
/// </summary>
public partial class QuotaAlertWindow : Window
{
    private const int PageSize = 12;
    private readonly MainViewModel _vm;
    private readonly List<QuotaAlertRow> _all = new();
    private int _page;

    public QuotaAlertWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        _all.Clear();
        foreach (RuleRow row in _vm.Rules)
        {
            if (row.Rule.QuotaBytes > 0)
            {
                _all.Add(new QuotaAlertRow(row));
            }
        }

        _all.Sort(static (a, b) => (b.IsOverflow ? 1 : 0).CompareTo(a.IsOverflow ? 1 : 0));
        RenderPage();
    }

    private void RenderPage()
    {
        int count = _all.Count;
        int totalPages = Math.Max(1, (count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, totalPages - 1);

        PageText.Text = count == 0
            ? "no quotas configured"
            : $"page {_page + 1} / {totalPages}  ·  {count} quota rule(s)";

        PrevBtn.IsEnabled = _page > 0;
        NextBtn.IsEnabled = _page < totalPages - 1;

        RowsHost.ItemsSource = _all.Skip(_page * PageSize).Take(PageSize).ToList();
    }

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        _page--;
        RenderPage();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        RenderPage();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is QuotaAlertRow row)
        {
            _vm.KillConnectionsForQuota(row.Row);
            Refresh();
        }
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is QuotaAlertRow row)
        {
            _vm.IgnoreQuota(row.Row);
            Refresh();
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is QuotaAlertRow row)
        {
            _vm.ResetQuota(row.Row);
            Refresh();
        }
    }
}
