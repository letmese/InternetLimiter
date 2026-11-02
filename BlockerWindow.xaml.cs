using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Valve.Core;

namespace Valve;

/// <summary>One application row in the blocker list.</summary>
public sealed class BlockerRow : Observable
{
    private static readonly Brush DenyBrush = Freeze(Color.FromRgb(0xE2, 0x60, 0x4F));
    private static readonly Brush AllowBrush = Freeze(Color.FromRgb(0x35, 0xD3, 0x9A));
    private static readonly Brush UnsetBrush = Freeze(Color.FromRgb(0x88, 0x92, 0xA3));

    public BlockerRow(AppRule rule) => Rule = rule;

    public AppRule Rule { get; }
    public string App => Rule.App;

    public BlockerState In
    {
        get => Rule.BlockerIn;
        set
        {
            Rule.BlockerIn = value;
            Raise(nameof(InText));
            Raise(nameof(InBrush));
        }
    }

    public BlockerState Out
    {
        get => Rule.BlockerOut;
        set
        {
            Rule.BlockerOut = value;
            Raise(nameof(OutText));
            Raise(nameof(OutBrush));
        }
    }

    public string InText => Rule.BlockerIn.Label();
    public string OutText => Rule.BlockerOut.Label();
    public Brush InBrush => BrushFor(Rule.BlockerIn);
    public Brush OutBrush => BrushFor(Rule.BlockerOut);

    private static Brush BrushFor(BlockerState state) => state switch
    {
        BlockerState.Deny => DenyBrush,
        BlockerState.Allow => AllowBrush,
        _ => UnsetBrush,
    };

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public partial class BlockerWindow : Window
{
    private readonly MainViewModel _vm;

    public BlockerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        EnabledBox.IsChecked = vm.CurrentSettings.BlockerEnabled;

        var rows = vm.CurrentSettings.Apps
            .OrderBy(a => a.App, StringComparer.OrdinalIgnoreCase)
            .Select(a => new BlockerRow(a))
            .ToList();

        RowsHost.ItemsSource = rows;

        if (rows.Count == 0)
        {
            HintText.Text = "no applications are configured yet — set a rule on the Activity tab first";
        }
    }

    private async void Enabled_Click(object sender, RoutedEventArgs e)
    {
        _vm.CurrentSettings.BlockerEnabled = EnabledBox.IsChecked == true;
        _vm.ApplySettings();
        await _vm.RequestFirewallSyncAsync();
    }

    private async void State_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BlockerRow row, Tag: string direction })
        {
            return;
        }

        if (direction == "In")
        {
            row.In = row.In.Next();
        }
        else
        {
            row.Out = row.Out.Next();
        }

        _vm.PersistSettings();
        await _vm.RequestFirewallSyncAsync();
    }

    private void ShowPending_Click(object sender, RoutedEventArgs e)
    {
        var window = new PendingRequestsWindow(_vm) { Owner = this };
        window.ShowDialog();
    }
}
