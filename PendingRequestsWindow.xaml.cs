using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Valve.Core;

namespace Valve;

public sealed class PendingRequestItem : Observable
{
    public ulong PacketId { get; set; }
    public string FlowKey { get; set; } = "";
    public string HeaderText { get; set; } = "Outgoing connection - TCP";
    public string AppName { get; set; } = "";
    public string Path { get; set; } = "";
    public int Pid { get; set; }
    public string PidText => $"Process {Pid}";
    public string LocalEndpoint { get; set; } = "";
    public string RemoteEndpoint { get; set; } = "";
    public ImageSource? Icon { get; set; }
}

public partial class PendingRequestsWindow : Window
{
    private readonly MainViewModel _vm;
    public ObservableCollection<PendingRequestItem> Items { get; }

    public PendingRequestsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Items = vm.PendingRequests; // live queue fed by MainViewModel.MaybeEnqueuePendingRequest
        RequestsHost.ItemsSource = Items;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusNote.Text = Items.Count == 0
            ? "No pending connection requests."
            : $"{Items.Count} pending connection request(s).";
    }

    private static bool RememberChecked(FrameworkElement button)
    {
        FrameworkElement? parent = button.Parent as FrameworkElement;
        while (parent is not null && parent is not StackPanel)
        {
            parent = parent.Parent as FrameworkElement;
        }

        if (parent is StackPanel row)
        {
            foreach (object child in row.Children)
            {
                if (child is CheckBox { Name: "RememberBox" } cb)
                {
                    return cb.IsChecked == true;
                }
            }
        }

        return true; // default: persist the decision
    }

    private async void Allow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PendingRequestItem item })
        {
            await _vm.ResolvePendingRequest(item, allow: true, remember: RememberChecked((FrameworkElement)sender));
        }
    }

    private async void Deny_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PendingRequestItem item })
        {
            await _vm.ResolvePendingRequest(item, allow: false, remember: RememberChecked((FrameworkElement)sender));
        }
    }

    private async void TemporaryAllow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PendingRequestItem item })
        {
            _vm.DismissPendingRequest(item);
            if (_vm.Processes.FirstOrDefault(p => p.Pid == item.Pid) is { } process)
            {
                await _vm.TemporaryBlockAsync(process, deny: false);
            }
        }
    }

    private async void TemporaryDeny_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PendingRequestItem item })
        {
            _vm.DismissPendingRequest(item);
            if (_vm.Processes.FirstOrDefault(p => p.Pid == item.Pid) is { } process)
            {
                await _vm.TemporaryBlockAsync(process, deny: true);
            }
        }
    }
}
