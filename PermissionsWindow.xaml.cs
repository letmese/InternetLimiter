using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Valve.Core;

namespace Valve;

/// <summary>
/// Permissions Tool (NetLimiter Sec 16, Items 16.01, 16.02, 16.02.a, 16.02.b, 16.02.c, 16.03).
/// Allows viewing and configuring Windows User Accounts and Groups with Monitor, Control, and Remote Connect rights,
/// as well as Remote Administration command permissions.
/// </summary>
public partial class PermissionsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<WindowsUserPermission> _windowsPerms;
    private readonly ObservableCollection<PermissionEntry> _remoteEntries;

    public PermissionsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        // Populate Windows User/Group Permissions
        var currentWin = _vm.CurrentSettings.WindowsPermissions;
        if (currentWin == null || currentWin.Count == 0)
        {
            currentWin = WindowsAclManager.GetDefaultPermissions();
        }

        _windowsPerms = new ObservableCollection<WindowsUserPermission>(
            currentWin.Select(p => new WindowsUserPermission(p.AccountName, p.AllowMonitor, p.AllowControl, p.AllowRemoteConnect)));
        WindowsPermGrid.ItemsSource = _windowsPerms;

        // Populate Remote Command Permissions
        _remoteEntries = new ObservableCollection<PermissionEntry>(
            _vm.CurrentSettings.Permissions.Select(p => new PermissionEntry
            {
                Type = p.Type,
                Allowed = p.Allowed,
                Note = p.Note,
            }));
        PermGrid.ItemsSource = _remoteEntries;
    }

    private void AddWindowsAccount_Click(object sender, RoutedEventArgs e)
    {
        _windowsPerms.Add(new WindowsUserPermission(@"BUILTIN\Users", monitor: true, control: false, remoteConnect: false));
    }

    private void RemoveWindowsAccount_Click(object sender, RoutedEventArgs e)
    {
        if (WindowsPermGrid.SelectedItem is WindowsUserPermission selected)
        {
            _windowsPerms.Remove(selected);
        }
    }

    private void ResetWindowsDefaults_Click(object sender, RoutedEventArgs e)
    {
        _windowsPerms.Clear();
        foreach (var p in WindowsAclManager.GetDefaultPermissions())
        {
            _windowsPerms.Add(new WindowsUserPermission(p.AccountName, p.AllowMonitor, p.AllowControl, p.AllowRemoteConnect));
        }
    }

    private void AddDefaults_Click(object sender, RoutedEventArgs e)
    {
        string[] types = { "SETBLOCK", "RESET", "SETLIMIT", "SETPRIORITY", "KILL", "EMERGENCY" };
        foreach (string type in types)
        {
            if (_remoteEntries.Any(x => string.Equals(x.Type, type, System.StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _remoteEntries.Add(new PermissionEntry { Type = type, Allowed = false, Note = "Denied by default" });
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 1. Commit Windows User Account / Group permissions
        _vm.CurrentSettings.WindowsPermissions = _windowsPerms.ToList();

        // 2. Commit Remote Command permissions
        _vm.CurrentSettings.Permissions.Clear();
        foreach (PermissionEntry entry in _remoteEntries)
        {
            _vm.CurrentSettings.Permissions.Add(entry);
        }

        _vm.PersistSettings();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
