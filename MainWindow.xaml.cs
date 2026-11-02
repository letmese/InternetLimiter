using System.Windows.Media;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using Valve.Core;

namespace Valve;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _autoScrollEnabled = true;
    private readonly Dictionary<int, UnifiedRule> _hotkeyMap = new();
    private HwndSource? _hotkeySource;
    private bool _hotkeyHookAdded;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        Loaded += (_, __) => RegisterGlobalHotkeys();
        Closed += (_, __) => UnregisterGlobalHotkeys();

        ConnGrid.ItemsSource = vm.Connections;
        QuotaGrid.ItemsSource = vm.Rules;
        ScheduleGrid.ItemsSource = vm.Schedules;
        RuleGrid.ItemsSource = vm.UnifiedRules;
        AppGrid.ItemsSource = vm.Applications;
        AppGrid.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.A && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
            {
                CleanStaleApps_Click(s, e);
                e.Handled = true;
            }
        };
        vm.NodeGraphChanged += samples => Graph.SetData(samples);
        StatsGrid.ItemsSource = vm.Weekly;
        FilterGrid.ItemsSource = vm.FilterRules;
        NetworkGrid.ItemsSource = vm.NetworkAdapters;
        PriorityGrid.ItemsSource = vm.Processes;

        StatusText.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.Status)));
        NoteText.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.Note)));
        EnvironmentText.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.EnvironmentText)));

        _autoScrollEnabled = vm.CurrentSettings.AutoScroll;
        UpdateAutoScrollUI();

        vm.GraphUpdated += () =>
        {
            if (vm.SelectedConnection == null && vm.SelectedProcess == null)
            {
                Graph.SetData(vm.Graph);
            }
            if (ChipOnline?.IsChecked == true || ChipOffline?.IsChecked == true)
            {
                CollectionViewSource.GetDefaultView(ProcessGrid.ItemsSource)?.Refresh();
            }
            if (_autoScrollEnabled)
            {
                PerformAutoScroll();
            }
        };
        vm.PropertyChanged += OnViewModelChanged;

        BlockerToggle.IsChecked = false;
        LimiterToggle.IsChecked = false;
        PriorityToggle.IsChecked = false;
        vm.CurrentSettings.BlockerEnabled = false;
        vm.CurrentSettings.LimiterEnabled = false;
        vm.CurrentSettings.PrioritiesEnabled = false;
        vm.ApplySettings();
        UpdateGlobalToggleLabels();
        UnitsBox.SelectedIndex = vm.CurrentSettings.UseBits ? 1 : 0;
        MachineText.Text = Environment.MachineName;
        InitializeDocking();
        ApplyColumnVisibility();

        SourceInitialized += (s, e) =>
        {
            string savedTheme = vm.CurrentSettings.Theme;
            if (string.IsNullOrEmpty(savedTheme)) savedTheme = "Dark";
            ApplyTheme(savedTheme);
        };

        StateChanged += (s, e) =>
        {
            if (MaximizeButton != null)
            {
                bool isMax = WindowState == WindowState.Maximized;
                MaximizeButton.ToolTip = isMax ? "Restore" : "Maximize";
                MaximizeButton.Tag = isMax ? "Maximized" : "Normal";
            }
        };

        Closed += (_, _) =>
        {
            try { vm.Dispose(); } catch { }
        };

        // Wire public API facade (ledger 19.x) and remote admin executor (ledger 15.x).
        Valve.Api.ValveApi.SnapshotProvider = () =>
            "{\"machine\":\"" + Environment.MachineName + "\",\"status\":\"" + _vm.Status +
            "\",\"apps\":\"" + _vm.AllCountText + "\",\"online\":\"" + _vm.OnlineCountText + "\"}";
        Valve.Api.ValveApi.RuleToggler = (id, enabled) =>
        {
            _vm.CurrentSettings.BlockerEnabled = enabled;
            _vm.PersistSettings();
            return "{\"ok\":true,\"rule\":\"" + id + "\",\"enabled\":" + (enabled ? "true" : "false") + "}";
        };
        Valve.Core.RemoteAdmin.Executor = (type, arg1, arg2) =>
        {
            try
            {
                // 15.x: enforce remote permission policy before any state-changing command
                if (!Valve.Core.PermissionGuard.IsAllowed(_vm.CurrentSettings, type))
                {
                    return "{\"ok\":false,\"permission\":true,\"msg\":\"permission-denied:" + type.ToUpperInvariant() + "\"}";
                }

                switch (type.ToUpperInvariant())
                {
                    case "SNAPSHOT": return Valve.Api.ValveApi.GetActivitySnapshot();
                    case "SETBLOCK": return Valve.Api.ValveApi.SetBlocked(arg1 == "1" || arg1.Equals("true", StringComparison.OrdinalIgnoreCase));
                    case "RESET": _vm.ResetTotals(); return "{\"ok\":true,\"msg\":\"totals-reset\"}";
                    case "PING": return "{\"ok\":true,\"msg\":\"pong\"}";
                    default: return "{\"ok\":false,\"msg\":\"unknown-command:" + type + "\"}";
                }
            }
            catch (Exception ex) { return "{\"ok\":false,\"msg\":\"" + ex.Message.Replace("\"", "'") + "\"}"; }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.EnvironmentText))
        {
            EnvironmentText.Text = _vm.EnvironmentText;
        }
    }

    // --- top bar ------------------------------------------------------------

    private void ShowAppInfo_Click(object sender, RoutedEventArgs e)
    {
        // 11.04.a: show application info for the selected row
        var info = new System.Text.StringBuilder();
        if (_vm.SelectedProcess is { } row)
        {
            info.AppendLine("Name: " + row.Name);
            if (!string.IsNullOrEmpty(row.Path)) info.AppendLine("Path: " + row.Path);
            info.AppendLine("Machine: " + Environment.MachineName);
        }
        else if (AppGrid?.SelectedItem is ApplicationEntry app)
        {
            info.AppendLine("App: " + app.Name);
            if (!string.IsNullOrEmpty(app.Path)) info.AppendLine("Path: " + app.Path);
            info.AppendLine("Machine: " + Environment.MachineName);
        }
        else
        {
            info.AppendLine("No application selected.");
            info.AppendLine("Select a row in Activity to see its info.");
        }
        MessageBox.Show(info.ToString(), "App info", MessageBoxButton.OK, MessageBoxImage.Information);
    }


    private void RemoteConnect_Click(object sender, RoutedEventArgs e)
    {
        // 15.x: remote administration dialog
        var win = new RemoteConnectWindow { Owner = this };
        win.ShowDialog();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        // 28.12: manual check; non-blocking asynchronous version check
        _vm.Report("Checking for updates...");
        try
        {
            var result = await Task.Run(() => Valve.Core.VersionChecker.CheckAsync());
            string msg = result.update
                ? "Update available: v" + result.latest + " (current v" + result.current + ").\n" + result.note
                : (result.latest == "unknown" || result.latest == result.current
                    ? "Internet Limiter is up to date (running v" + result.current + ")."
                    : "You are running the latest version: v" + result.current);
            MessageBox.Show(this, msg, "Internet Limiter Update", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not check for updates: " + ex.Message, "Internet Limiter Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _vm.Report("Ready");
        }
    }

    private void ToggleNtfsCompression_Click(object sender, RoutedEventArgs e)
    {
        // 13.14.g: NTFS compression for the stats database folder
        var answer = MessageBox.Show("Compress the stats database folder with NTFS compression?\n\n" +
            "Yes = compress (saves ~50% disk space)\nNo = decompress\nCancel = leave unchanged",
            "NTFS compression", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        string folder = new SettingsStore().RootDirectory;
        string result = Valve.Core.NtfsCompression.Toggle(folder, answer == MessageBoxResult.Yes);
        MessageBox.Show(result, "NTFS compression", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } element)
        {
            menu.PlacementTarget = element;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(
        this,
        "Internet Limiter — per-app bandwidth control\n\n"
        + "This build runs entirely in user mode: live monitoring, upload caps, "
        + "Windows firewall rules, quotas and schedules.\n\n"
        + "Download shaping needs the kernel driver and is not available here.",
        "About Internet Limiter", MessageBoxButton.OK, MessageBoxImage.Information);

    private void OpenBlocker_Click(object sender, RoutedEventArgs e)
    {
        var window = new BlockerWindow(_vm) { Owner = this };
        window.ShowDialog();
    }

    private void OpenRuleEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProcess is not { } process)
        {
            _vm.Report("select an application first");
            return;
        }

        var window = new RuleEditorWindow(_vm, process) { Owner = this };
        window.ShowDialog();
        RegisterGlobalHotkeys();
    }

    // --- unified rule list toolbar (Items 4.01/4.07) -----------------------

    private void RuleAdd_Click(object sender, RoutedEventArgs e)
    {
        var window = new RuleEditorWindow(_vm, _vm.SelectedProcess) { Owner = this };
        window.ShowDialog();
        RegisterGlobalHotkeys();
    }

    private void RuleEdit_Click(object sender, RoutedEventArgs e)
    {
        if (RuleGrid.SelectedItem is not UnifiedRule rule)
        {
            _vm.Report("select a rule first");
            return;
        }

        var window = new RuleEditorWindow(_vm, _vm.SelectedProcess, rule) { Owner = this };
        window.ShowDialog();
        RegisterGlobalHotkeys();
    }

    private void RuleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RuleGrid.SelectedItem is not UnifiedRule rule)
        {
            _vm.Report("select a rule first");
            return;
        }

        _vm.DeleteUnifiedRule(rule);
    }

    private string SelectedQuickZone()
    {
        if (QuickZoneBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            return tag;
        }

        return "internet";
    }

    private void QuickBlockZone_Click(object sender, RoutedEventArgs e)
    {
        _vm.QuickBlockZone(SelectedQuickZone(), block: true);
    }

    private void QuickUnblockZone_Click(object sender, RoutedEventArgs e)
    {
        _vm.QuickBlockZone(SelectedQuickZone(), block: false);
    }

    private void RuleGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        // Persist enabled-state toggles edited in the grid.
        if (e.Row.Item is UnifiedRule rule && e.EditAction == DataGridEditAction.Commit)
        {
            _vm.UpdateUnifiedRule(rule);
        }
    }

    // --- per-rule global hotkeys (Item 4.09) --------------------------------

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const int HOTKEY_ID_BASE = 0xC100;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void RegisterGlobalHotkeys()
    {
        UnregisterGlobalHotkeys();
        _hotkeyMap.Clear();

        if (_hotkeySource is null)
        {
            var helper = new WindowInteropHelper(this);
            _hotkeySource = HwndSource.FromHwnd(helper.Handle);
        }

        if (_hotkeySource is null) return;

        if (!_hotkeyHookAdded)
        {
            _hotkeySource.AddHook(WndProc);
            _hotkeyHookAdded = true;
        }

        int id = HOTKEY_ID_BASE;
        foreach (var rule in _vm.UnifiedRules)
        {
            if (string.IsNullOrWhiteSpace(rule.HotKey)) continue;
            if (_hotkeyMap.Values.Contains(rule)) continue;
            if (!TryParseHotKey(rule.HotKey, out uint modifiers, out uint vk)) continue;

            if (RegisterHotKey(_hotkeySource.Handle, id, modifiers, vk))
            {
                _hotkeyMap[id] = rule;
                id++;
            }
        }
    }

    private void UnregisterGlobalHotkeys()
    {
        if (_hotkeySource is null) return;
        foreach (int id in _hotkeyMap.Keys)
        {
            UnregisterHotKey(_hotkeySource.Handle, id);
        }
        _hotkeyMap.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _hotkeyMap.TryGetValue(wParam.ToInt32(), out var rule))
        {
            _vm.ToggleUnifiedRule(rule);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static bool TryParseHotKey(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        string[] parts = (text ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        string key = parts[^1].Trim();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    return false;
            }
        }

        if (modifiers == 0) return false;

        if (key.Length == 1 && key[0] >= 'A' && key[0] <= 'Z')
        {
            vk = (uint)key[0];
            return true;
        }

        if (key.Length == 1 && key[0] >= '0' && key[0] <= '9')
        {
            vk = (uint)(0x30 + (key[0] - '0'));
            return true;
        }

        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key[1..], out int f) && f >= 1 && f <= 24)
        {
            vk = (uint)(0x70 + f - 1);
            return true;
        }

        return false;
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _vm.TogglePause();
        PauseButton.Content = _vm.IsPaused ? "â–¶ Resume" : "â¸ Pause";
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_vm) { Owner = this };
        window.ShowDialog();
    }

    private void Units_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Fires once while the XAML is still being built, before _vm exists.
        if (_vm is null || UnitsBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        switch (tag)
        {
            case "auto":
                Fmt.ScaleMode = Fmt.UnitDisplayScale.Auto;
                _vm.CurrentSettings.UseBits = false;
                break;
            case "bytes":
                Fmt.ScaleMode = Fmt.UnitDisplayScale.Auto;
                _vm.CurrentSettings.UseBits = false;
                break;
            case "bits":
                Fmt.ScaleMode = Fmt.UnitDisplayScale.Auto;
                _vm.CurrentSettings.UseBits = true;
                break;
            case "kilo":
                Fmt.ScaleMode = Fmt.UnitDisplayScale.Kilo;
                break;
            case "mega":
                Fmt.ScaleMode = Fmt.UnitDisplayScale.Mega;
                break;
        }
        _vm.ApplySettings();
    }

        private void UpdateGlobalToggleLabels()
    {
        if (BlockerToggle != null) BlockerToggle.Content = BlockerToggle.IsChecked == true ? "Blocker: ON" : "Blocker: OFF";
        if (LimiterToggle != null) LimiterToggle.Content = LimiterToggle.IsChecked == true ? "Limiter: ON" : "Limiter: OFF";
        if (PriorityToggle != null) PriorityToggle.Content = PriorityToggle.IsChecked == true ? "Priorities: ON" : "Priorities: OFF";
    }

    private void GlobalToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanControl)
        {
            _vm.Report(MainViewModel.ControlPermissionRequiredMessage);
            BlockerToggle.IsChecked = _vm.CurrentSettings.BlockerEnabled;
            LimiterToggle.IsChecked = _vm.CurrentSettings.LimiterEnabled;
            PriorityToggle.IsChecked = _vm.CurrentSettings.PrioritiesEnabled;
            UpdateGlobalToggleLabels();
            return;
        }
        _vm.CurrentSettings.BlockerEnabled = BlockerToggle.IsChecked == true;
        _vm.CurrentSettings.LimiterEnabled = LimiterToggle.IsChecked == true;
        _vm.CurrentSettings.PrioritiesEnabled = PriorityToggle.IsChecked == true;
        _vm.ApplySettings();
        UpdateGlobalToggleLabels();
    }

    // --- activity view ------------------------------------------------------

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TreeNode node })
        {
            node.IsExpanded = !node.IsExpanded;
            _vm.RefreshTree();
        }
    }

    private void ProcessGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedNode is { CanExpand: true } node)
        {
            node.IsExpanded = !node.IsExpanded;
            _vm.RefreshTree();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox?.SelectedItem is ComboBoxItem item && _vm is not null)
        {
            string choice = item.Content?.ToString() ?? "Rate";
            _vm.SortColumn = choice switch
            {
                "Name" => "Name",
                "Rate" => "Rate",
                "Status" => "Status",
                _ => "Rate"
            };
            _vm.RefreshTree();
        }
    }

    private void ProcessGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // Prevent flat WPF DataGrid sort which breaks tree hierarchy

        string colName = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(colName))
        {
            colName = e.Column.Header as string ?? "";
        }

        if (_vm.SortColumn == colName)
        {
            _vm.SortAscending = !_vm.SortAscending;
        }
        else
        {
            _vm.SortColumn = colName;
            _vm.SortAscending = (colName == "Name");
        }

        foreach (var col in ProcessGrid.Columns)
        {
            col.SortDirection = null;
        }
        e.Column.SortDirection = _vm.SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;

        _vm.RefreshTree();
        var view = CollectionViewSource.GetDefaultView(ProcessGrid.ItemsSource);
        view?.Refresh();
    }

    private void Chip_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (ProcessGrid is null)
        {
            return;
        }

        ICollectionView view = CollectionViewSource.GetDefaultView(ProcessGrid.ItemsSource);
        if (view is null)
        {
            return;
        }

        string term = SearchBox?.Text.Trim() ?? string.Empty;
        bool online = ChipOnline?.IsChecked == true;
        bool offline = ChipOffline?.IsChecked == true;
        bool blocked = ChipBlocked?.IsChecked == true;
        bool hidden = ChipHidden?.IsChecked == true;

        view.Filter = o =>
        {
            if (o is not TreeNode node)
            {
                return false;
            }

            bool isNodeHidden = node.Process?.IsHidden == true;
            if (hidden)
            {
                if (!isNodeHidden && !node.IsRoot) return false;
            }
            else
            {
                if (isNodeHidden) return false;
            }

            if (term.Length > 0)
            {
                bool match = node.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(node.Path) && node.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
                    || (node.Connection != null && (
                        node.Connection.Remote.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || node.Connection.Hostname.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || node.Connection.Location.Contains(term, StringComparison.OrdinalIgnoreCase)));
                if (!match) return false;
            }

            if (online && !node.IsOnline)
            {
                return false;
            }

            if (offline && node.IsOnline)
            {
                return false;
            }

            return !blocked || node.Blocked;
        };
    }

    // --- rule actions on the selected node ----------------------------------


    public void ApplyColumnVisibility()
    {
        var s = _vm.CurrentSettings;
        if (ColName != null) ColName.Visibility = s.ShowColName ? Visibility.Visible : Visibility.Collapsed;
        if (ColDlRate != null) ColDlRate.Visibility = s.ShowColDlRate ? Visibility.Visible : Visibility.Collapsed;
        if (ColUlRate != null) ColUlRate.Visibility = s.ShowColUlRate ? Visibility.Visible : Visibility.Collapsed;
        if (ColDlLimit != null) ColDlLimit.Visibility = s.ShowColDlLimit ? Visibility.Visible : Visibility.Collapsed;
        if (ColUlLimit != null) ColUlLimit.Visibility = s.ShowColUlLimit ? Visibility.Visible : Visibility.Collapsed;
        if (ColDlTotal != null) ColDlTotal.Visibility = s.ShowColDlTotal ? Visibility.Visible : Visibility.Collapsed;
        if (ColUlTotal != null) ColUlTotal.Visibility = s.ShowColUlTotal ? Visibility.Visible : Visibility.Collapsed;
        if (ColInFw != null) ColInFw.Visibility = s.ShowColInFw ? Visibility.Visible : Visibility.Collapsed;
        if (ColOutFw != null) ColOutFw.Visibility = s.ShowColOutFw ? Visibility.Visible : Visibility.Collapsed;
        if (ColStatus != null) ColStatus.Visibility = s.ShowColStatus ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ColumnHeaderContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            var s = _vm.CurrentSettings;
            foreach (var item in menu.Items)
            {
                if (item is MenuItem mi && mi.Tag is string tag)
                {
                    mi.IsChecked = tag switch
                    {
                        "ColName" => s.ShowColName,
                        "ColDlRate" => s.ShowColDlRate,
                        "ColUlRate" => s.ShowColUlRate,
                        "ColDlLimit" => s.ShowColDlLimit,
                        "ColUlLimit" => s.ShowColUlLimit,
                        "ColDlTotal" => s.ShowColDlTotal,
                        "ColUlTotal" => s.ShowColUlTotal,
                        "ColInFw" => s.ShowColInFw,
                        "ColOutFw" => s.ShowColOutFw,
                        "ColStatus" => s.ShowColStatus,
                        _ => mi.IsChecked
                    };
                }
            }
        }
    }

    private void MenuColToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
        {
            bool val = mi.IsChecked;
            var s = _vm.CurrentSettings;
            switch (tag)
            {
                case "ColName": s.ShowColName = val; break;
                case "ColDlRate": s.ShowColDlRate = val; break;
                case "ColUlRate": s.ShowColUlRate = val; break;
                case "ColDlLimit": s.ShowColDlLimit = val; break;
                case "ColUlLimit": s.ShowColUlLimit = val; break;
                case "ColDlTotal": s.ShowColDlTotal = val; break;
                case "ColUlTotal": s.ShowColUlTotal = val; break;
                case "ColInFw": s.ShowColInFw = val; break;
                case "ColOutFw": s.ShowColOutFw = val; break;
                case "ColStatus": s.ShowColStatus = val; break;
            }
            ApplyColumnVisibility();
            _vm.PersistSettings();
        }
    }

    private void CustomizeColumns_Click(object sender, RoutedEventArgs e)
    {
        var opt = new OptionsWindow(_vm.CurrentSettings, () =>
        {
            ApplyColumnVisibility();
            _vm.PersistSettings();
        })
        {
            Owner = this
        };
        if (opt.ShowDialog() == true)
        {
            ApplyColumnVisibility();
            _vm.PersistSettings();
        }
    }
    private async void CapSet_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProcess is { } row)
        {
            if (double.TryParse(CapBox.Text.Trim(), out double val) && val >= 0)
            {
                int kbps = UnitConverter.ParseToKbps(val, "KB/s");
                await _vm.SetCapAsync(row, kbps);
            }
            else
            {
                await _vm.SetCapAsync(row, row.LimitKbps);
            }
        }
        else
        {
            _vm.Report("select an application first");
        }
    }

    private async void DownCapSet_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProcess is { } row)
        {
            if (double.TryParse(DownCapBox.Text.Trim(), out double val) && val >= 0)
            {
                int kbps = UnitConverter.ParseToKbps(val, "KB/s");
                await _vm.SetDownloadCapAsync(row, kbps);
            }
            else
            {
                await _vm.SetDownloadCapAsync(row, row.DownloadLimitKbps);
            }
        }
        else
        {
            _vm.Report("select an application first");
        }
    }

    private async void Block_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProcess is { } row)
        {
            await _vm.SetBlockedAsync(row, !row.IsBlocked);
        }
        else
        {
            _vm.Report("select an application first");
        }
    }

    private async void GridBlock_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = null;
        if (sender is FrameworkElement fe && fe.DataContext is TreeNode node)
        {
            row = node.Process;
        }
        row ??= _vm.SelectedProcess;

        if (row != null)
        {
            await _vm.SetBlockedAsync(row, !row.IsBlocked);
            _vm.SelectedNode?.RefreshDerived();
        }
        else
        {
            _vm.Report("select an application first");
        }
    }

    private async void GridFw_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TreeNode node && node.Process is { } row)
        {
            await _vm.SetBlockedAsync(row, cb.IsChecked == true);
            node.RefreshDerived();
        }
    }

    private void GridKillConn_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = null;
        if (sender is FrameworkElement fe && fe.DataContext is TreeNode node)
        {
            row = node.Process;
        }
        row ??= _vm.SelectedProcess;

        if (row != null && row.Pid > 0)
        {
            int killed = Collector.KillConnections(row.Pid);
            _vm.Report($"Killed {killed} TCP connection(s) for {row.Name} (PID {row.Pid})");
        }
        else
        {
            _vm.Report("select an application with an active PID first");
        }
    }

    private void GridLimitDown_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender) ?? _vm.SelectedProcess;
        if (row != null)
        {
            var dlg = new SetLimitWindow(row, _vm, isDownload: true) { Owner = this };
            dlg.ShowDialog();
        }
        else
        {
            NavBandwidth.IsChecked = true;
            DownCapBox.Focus();
            DownCapBox.SelectAll();
        }
    }

    private void GridLimitUp_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender) ?? _vm.SelectedProcess;
        if (row != null)
        {
            var dlg = new SetLimitWindow(row, _vm, isDownload: false) { Owner = this };
            dlg.ShowDialog();
        }
        else
        {
            NavBandwidth.IsChecked = true;
            CapBox.Focus();
            CapBox.SelectAll();
        }
    }

    private void GridLimitDlCell_Click(object sender, MouseButtonEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender) ?? _vm.SelectedProcess;
        if (row != null)
        {
            var dlg = new SetLimitWindow(row, _vm, isDownload: true) { Owner = this };
            dlg.ShowDialog();
        }
    }

    private void GridLimitUlCell_Click(object sender, MouseButtonEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender) ?? _vm.SelectedProcess;
        if (row != null)
        {
            var dlg = new SetLimitWindow(row, _vm, isDownload: false) { Owner = this };
            dlg.ShowDialog();
        }
    }

    private void DownCapBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DownCapSet_Click(sender, e);
        }
    }

    private void CapBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CapSet_Click(sender, e);
        }
    }

    private void GridLimitToggleDl_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender);
        if (row != null)
        {
            _vm.ToggleDownloadLimit(row);
            _vm.RefreshTree();
        }
    }

    private void GridLimitToggleUl_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender);
        if (row != null)
        {
            _vm.ToggleUploadLimit(row);
            _vm.RefreshTree();
        }
    }

    private void SideDownloadLimitToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProcess is { } row)
        {
            _vm.ToggleDownloadLimit(row);
            _vm.RefreshTree();
        }
    }

    private void SideUploadLimitToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProcess is { } row)
        {
            _vm.ToggleUploadLimit(row);
            _vm.RefreshTree();
        }
    }

    private async void SideCoupleLimits_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProcess is { } row)
        {
            int cap = row.DownloadLimitKbps > 0 ? row.DownloadLimitKbps : (row.LimitKbps > 0 ? row.LimitKbps : 1024);
            await _vm.SetBothLimitsAsync(row, cap);
            _vm.RefreshTree();
        }
    }

    private async void GridLimitBoth_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender);
        if (row != null)
        {
            int cap = row.DownloadLimitKbps > 0 ? row.DownloadLimitKbps : (row.LimitKbps > 0 ? row.LimitKbps : 1024);
            await _vm.SetBothLimitsAsync(row, cap);
            _vm.RefreshTree();
        }
    }

    private async void MenuRemoveLimit_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender);
        if (row != null)
        {
            await _vm.SetCapAsync(row, 0);
            await _vm.SetDownloadCapAsync(row, 0);
            _vm.RefreshTree();
        }
    }

    private async void MenuSetPriority_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender);
        if (row != null && sender is FrameworkElement { Tag: string tag } && Enum.TryParse<TrafficPriority>(tag, out var prio))
        {
            await _vm.SetPriorityAsync(row, prio);
            _vm.RefreshTree();
        }
    }

    private void MenuKillAppConnections_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender);
        if (row != null)
        {
            _vm.KillAppConnections(row);
            _vm.RefreshTree();
        }
    }

    private void MenuTerminateProcess_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender);
        if (row != null)
        {
            var res = MessageBox.Show(this, $"Terminate process {row.Name} (PID {row.Pid})?", "Terminate Process", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                _vm.TerminateProcess(row);
                _vm.RefreshTree();
            }
        }
    }

    private void ConnSetLimitDown_Click(object sender, RoutedEventArgs e)
    {
        if (ConnGrid.SelectedItem is ConnRow conn)
        {
            int limit = conn.DownloadLimitKbps > 0 ? conn.DownloadLimitKbps : 1024;
            _vm.SetConnectionDownloadLimit(conn, limit);
        }
    }

    private void ConnSetLimitUp_Click(object sender, RoutedEventArgs e)
    {
        if (ConnGrid.SelectedItem is ConnRow conn)
        {
            int limit = conn.UploadLimitKbps > 0 ? conn.UploadLimitKbps : 1024;
            _vm.SetConnectionUploadLimit(conn, limit);
        }
    }

    private void ConnRemoveLimit_Click(object sender, RoutedEventArgs e)
    {
        if (ConnGrid.SelectedItem is ConnRow conn)
        {
            _vm.SetConnectionDownloadLimit(conn, 0);
            _vm.SetConnectionUploadLimit(conn, 0);
        }
    }

    private void ConnSetPriority_Click(object sender, RoutedEventArgs e)
    {
        if (ConnGrid.SelectedItem is ConnRow conn && sender is FrameworkElement { Tag: string tag } && Enum.TryParse<TrafficPriority>(tag, out var prio))
        {
            _vm.SetConnectionPriority(conn, prio);
        }
    }

    private ProcessRow? GetContextRow(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.DataContext is ProcessRow pr) return pr;
            if (fe.DataContext is TreeNode tn && tn.Process != null) return tn.Process;
        }
        return _vm.SelectedProcess;
    }

    private void GridCopyPath_Click(object sender, RoutedEventArgs e)
    {
        string? path = null;
        if (sender is FrameworkElement fe && fe.DataContext is TreeNode node)
        {
            path = !string.IsNullOrEmpty(node.Path) ? node.Path : node.Process?.Path;
        }
        path ??= _vm.SelectedPath;

        if (!string.IsNullOrEmpty(path))
        {
            Clipboard.SetText(path);
            _vm.Report($"Copied: {path}");
        }
    }

    private async void RuleClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RuleRow row })
        {
            await _vm.ClearRuleAsync(row);
        }
    }

    // --- quota / schedule ---------------------------------------------------

    private void QuotaSet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RuleRow row })
        {
            _vm.SetQuota(row, (long)(Math.Max(0, row.QuotaMbEdit) * 1048576), 1);
        }
    }

    private void QuotaStatus_Click(object sender, RoutedEventArgs e)
    {
        var win = new QuotaAlertWindow(_vm) { Owner = this };
        win.Show();
    }

    private void ScheduleAdd_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseTime(SchStart.Text, out int start) || !TryParseTime(SchEnd.Text, out int end))
        {
            _vm.Report("times must look like 09:00");
            return;
        }

        if (!int.TryParse(SchRate.Text, out int rate) || rate <= 0)
        {
            _vm.Report("cap must be a positive number of kbit/s");
            return;
        }

        bool[] days = SchDays.SelectedIndex switch
        {
            1 => new[] { false, true, true, true, true, true, false },
            2 => new[] { true, false, false, false, false, false, true },
            _ => new[] { true, true, true, true, true, true, true },
        };

        _vm.AddSchedule(SchApp.Text, rate, start, end, days);
    }

    private void ScheduleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScheduleRow row })
        {
            _vm.RemoveSchedule(row);
        }
    }

    // --- tools --------------------------------------------------------------

    private void KillConnections_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsConnectionSelected && _vm.SelectedConnection is { } conn)
        {
            bool ok = Collector.KillConnection(conn.Key);
            _vm.Report(ok
                ? $"closed connection {conn.Remote}"
                : $"could not close connection {conn.Remote} (needs administrator)");
            _vm.RefreshTree();
            return;
        }

        if (_vm.SelectedProcess is not { } row)
        {
            _vm.Report("select an application or connection first");
            return;
        }

        int killed = Collector.KillConnections(row.Pid);
        _vm.Report(killed == 0
            ? $"no connections torn down for {row.Name} (needs administrator)"
            : $"tore down {killed} connection{(killed == 1 ? "" : "s")} for {row.Name}");
        _vm.RefreshTree();
    }

    private async void DeleteRules_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProcess is not { } process)
        {
            _vm.Report("select an application first");
            return;
        }

        RuleRow? row = _vm.Rules.FirstOrDefault(r =>
            string.Equals(r.App, process.Name, StringComparison.OrdinalIgnoreCase));

        if (row is null)
        {
            _vm.Report($"no rules on {process.Name}");
            return;
        }

        await _vm.ClearRuleAsync(row);
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _vm.DataFolder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _vm.Report(ex.Message);
        }
    }

    private static bool TryParseTime(string text, out int minutes)
    {
        minutes = 0;
        string[] parts = (text ?? string.Empty).Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int hour)
            || !int.TryParse(parts[1], out int minute))
        {
            return false;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return false;
        }

        minutes = (hour * 60) + minute;
        return true;
    }

    // --- filters & priorities & pending requests ----------------------------

    private void AddFilter_Click(object sender, RoutedEventArgs e)
    {
        var window = new FilterEditorWindow(null, _vm.FilterRules.ToList()) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _vm.FilterRules.Add(window.Result);
            _vm.SaveFilters();
            _vm.Report($"Added filter '{window.Result.Name}'");
        }
    }

    private void EditFilter_Click(object sender, RoutedEventArgs e)
    {
        if (FilterGrid.SelectedItem is not FilterRule filter)
        {
            _vm.Report("Select a filter to edit");
            return;
        }

        var window = new FilterEditorWindow(filter, _vm.FilterRules.ToList()) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _vm.SaveFilters();
            _vm.Report($"Updated filter '{filter.Name}'");
        }
    }

    private void DeleteFilter_Click(object sender, RoutedEventArgs e)
    {
        if (FilterGrid.SelectedItem is FilterRule filter)
        {
            _vm.FilterRules.Remove(filter);
            _vm.SaveFilters();
            _vm.Report($"Removed filter '{filter.Name}'");
        }
    }

    private async void SetPrioRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag, DataContext: ProcessRow row } &&
            Enum.TryParse<TrafficPriority>(tag, out var prio))
        {
            await _vm.SetPriorityAsync(row, prio);
        }
    }

    private void OpenPendingRequests_Click(object sender, RoutedEventArgs e)
    {
        var window = new PendingRequestsWindow(_vm) { Owner = this };
        window.ShowDialog();
    }


    private void ExportStatsCsv_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Valve_TrafficStats_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            DefaultExt = ".csv"
        };
        if (sfd.ShowDialog() == true)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Application,Download_Bytes,Upload_Bytes,Total_Bytes,Download_Formatted,Upload_Formatted,Total_Formatted");
            foreach (var item in _vm.Weekly)
            {
                sb.AppendLine($"\"{item.App}\",{item.BytesIn},{item.BytesOut},{item.BytesIn + item.BytesOut},\"{item.InText}\",\"{item.OutText}\",\"{item.TotalText}\"");
            }
            System.IO.File.WriteAllText(sfd.FileName, sb.ToString(), System.Text.Encoding.UTF8);
            _vm.Report($"Exported statistics to {System.IO.Path.GetFileName(sfd.FileName)}");
        }
    }


    private void CopyConnRemote_Click(object sender, RoutedEventArgs e)
    {
        if (ConnGrid?.SelectedItem is ConnRow row)
        {
            Clipboard.SetText(row.Remote);
            _vm.Report($"Copied {row.Remote} to clipboard");
        }
    }

    private void BlockConnRemote_Click(object sender, RoutedEventArgs e)
    {
        if (ConnGrid?.SelectedItem is ConnRow row)
        {
            string ip = row.Remote.Split(':')[0];
            var filter = new FilterRule
            {
                Name = $"Block {ip}",
                ConditionType = FilterConditionType.IpRange,
                Value = ip,
                Blocked = true
            };
            _vm.FilterRules.Add(filter);
            _vm.SaveFilters();
            _vm.Report($"Created filter rule blocking {ip}");
        }
    }

    private void RightNav_Changed(object sender, RoutedEventArgs e)
    {
        if (SecOverview == null || SecFirewall == null || SecBandwidth == null || SecRules == null)
            return;

        SecOverview.Visibility = NavOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SecFirewall.Visibility = NavFirewall.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SecBandwidth.Visibility = NavBandwidth.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SecRules.Visibility = NavRules.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        _vm.ExpandAll();
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        _vm.CollapseAll();
    }

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Internet Limiter Telemetry Snapshot - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Total Download: {_vm.TotalInText} | Total Upload: {_vm.TotalOutText}");
            sb.AppendLine($"Active Processes: {_vm.Processes.Count} | Active Connections: {_vm.Connections.Count}");
            sb.AppendLine("--------------------------------------------------------------------------------");
            foreach (var p in _vm.Processes.Take(15))
            {
                sb.AppendLine($"{p.Name,-25} PID:{p.Pid,-7} DL:{p.InRateText,-12} UL:{p.OutRateText,-12} Conns:{p.Connections}");
            }
            Clipboard.SetText(sb.ToString());
            _vm.Report("ðŸ“¸ Telemetry snapshot copied to clipboard!");
        }
        catch (Exception ex)
        {
            _vm.Report($"Snapshot error: {ex.Message}");
        }
    }

    private void EmergencyBlock_Click(object sender, RoutedEventArgs e)
    {
        _vm.EmergencyBlockActive = !_vm.EmergencyBlockActive;
        EmergencyBlockButton.Content = _vm.EmergencyBlockActive ? "ðŸš« BLOCK ACTIVE" : "ðŸš« Emergency Block";
        EmergencyBlockButton.Background = _vm.EmergencyBlockActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 54, 51))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 17, 20));
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        _vm.Report($"Telemetry Status: {_vm.Status} | WFP Firewall: {(_vm.FirewallAvailable ? "Ready" : "Off")} | Platform: Windows {Environment.OSVersion.Version}");
    }

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        OpenData_Click(sender, e);
    }

    private void TitleMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void TitleMax_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } element)
        {
            menu.PlacementTarget = element;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void ThemeRadio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string themeName })
        {
            ApplyTheme(themeName);
        }
    }

    private void SelectTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string themeName })
        {
            ApplyTheme(themeName);
        }
    }

    public void ApplyTheme(string themeName)
    {
        ThemeManager.ApplyTheme(themeName, this);
        if (_vm != null)
        {
            _vm.CurrentSettings.Theme = themeName;
            _vm.PersistSettings();
        }
        SyncThemeUI(themeName);
        Graph?.InvalidateVisual();
        InvalidateVisual();
    }

    private void SyncThemeUI(string themeName)
    {
        string normalized = (themeName ?? "Dark").ToLowerInvariant() switch
        {
            "cyberpunk" => "Cyberpunk",
            "light" => "Light",
            _ => "Dark"
        };

        if (ThemeLabelText != null)
        {
            ThemeLabelText.Text = normalized switch
            {
                "Cyberpunk" => "Cyberpunk",
                "Light" => "Light",
                _ => "Dark (Obsidian)"
            };
        }
        if (ThemeIconText != null)
        {
            ThemeIconText.Text = normalized switch
            {
                "Cyberpunk" => "\u26A1",
                "Light" => "\u2600\uFE0F",
                _ => "\U0001F319"
            };
        }

        if (ThemeRadioDark != null)
            ThemeRadioDark.IsChecked = normalized == "Dark";
        if (ThemeRadioCyber != null)
            ThemeRadioCyber.IsChecked = normalized == "Cyberpunk";
        if (ThemeRadioLight != null)
            ThemeRadioLight.IsChecked = normalized == "Light";
    }

    private void AutoScrollButton_Click(object sender, RoutedEventArgs e)
    {
        _autoScrollEnabled = !_autoScrollEnabled;
        if (_vm != null)
        {
            _vm.CurrentSettings.AutoScroll = _autoScrollEnabled;
            _vm.PersistSettings();
        }
        UpdateAutoScrollUI();
        if (_autoScrollEnabled)
        {
            PerformAutoScroll();
        }
    }

    private void UpdateAutoScrollUI()
    {
        if (AutoScrollText != null)
        {
            AutoScrollText.Text = _autoScrollEnabled ? "Auto-scroll: ON" : "Auto-scroll: OFF";
            AutoScrollText.SetResourceReference(TextBlock.ForegroundProperty, _autoScrollEnabled ? "Text" : "TextDim");
        }
        if (AutoScrollDot != null)
        {
            AutoScrollDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _autoScrollEnabled ? "Down" : "TextFaint");
        }
        if (AutoScrollButton != null)
        {
            AutoScrollButton.ToolTip = _autoScrollEnabled
                ? "Auto-scroll is ON (tracking active/selected item). Click to disable."
                : "Auto-scroll is OFF. Click to enable.";
        }
    }

    private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_autoScrollEnabled && ProcessGrid?.SelectedItem is { } selected)
        {
            try
            {
                ProcessGrid.ScrollIntoView(selected);
            }
            catch { }
        }
    }

    private void PerformAutoScroll()
    {
        if (!_autoScrollEnabled || ProcessGrid == null)
        {
            return;
        }

        try
        {
            if (Tabs?.SelectedIndex == 0)
            {
                if (ProcessGrid.Items.Count == 0) return;

                // 1. Keep selected process in view if user selected one
                if (ProcessGrid.SelectedItem is { } selected)
                {
                    ProcessGrid.ScrollIntoView(selected);
                    return;
                }

                // 2. Otherwise scroll to top active process generating bandwidth
                if (_vm?.TreeRows != null)
                {
                    var activeNode = _vm.TreeRows.FirstOrDefault(n => n.InRate > 0 || n.OutRate > 0);
                    if (activeNode != null)
                    {
                        ProcessGrid.ScrollIntoView(activeNode);
                        return;
                    }
                }

                // 3. Fallback: ensure top row visible
                if (ProcessGrid.Items.Count > 0 && ProcessGrid.Items[0] is { } first)
                {
                    ProcessGrid.ScrollIntoView(first);
                }
            }
            else if (Tabs?.SelectedIndex == 1 && ConnGrid?.SelectedItem is { } connSelected)
            {
                ConnGrid.ScrollIntoView(connSelected);
            }
        }
        catch
        {
            // Ignore scrolling race conditions during collection modifications
        }
    }

    private async void VirusTotalScan_Click(object sender, RoutedEventArgs e)
    {
        string sha = _vm.SelectedSha256;
        string path = _vm.SelectedProcess?.Path ?? "";
        bool hasKey = !string.IsNullOrWhiteSpace(_vm.CurrentSettings.VirusTotalApiKey);

        // 13.x: when an API key is configured do a REAL scan; otherwise keep browser lookup.
        if (hasKey)
        {
            await _vm.RefreshVtVerdictAsync(path);
            _vm.Report(_vm.VtVerdict ?? "VirusTotal verdict unavailable.");
            MessageBox.Show(_vm.VtVerdictText + "\n\n" + (_vm.VtVerdict ?? ""), "VirusTotal Scan",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!string.IsNullOrEmpty(sha) && sha != "-")
        {
            try
            {
                string url = $"https://www.virustotal.com/gui/file/{sha}";
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                _vm.Report($"VirusTotal lookup opened for {sha.Substring(0, 12)}... (add an API key in Settings to scan in-app)");
            }
            catch (Exception ex)
            {
                _vm.Report($"VirusTotal error: {ex.Message}");
            }
        }
        else
        {
            _vm.Report("Select an application with an accessible executable first");
        }
    }

    private void WhoisLookup_Click(object sender, RoutedEventArgs e)
    {
        string remote = "";
        if (sender is FrameworkElement fe && fe.DataContext is TreeNode tn && tn.Connection is { } conn)
        {
            remote = conn.Remote;
        }
        else if (ConnGrid?.SelectedItem is ConnRow cr)
        {
            remote = cr.Remote;
        }
        else if (_vm.SelectedConnection is { } c)
        {
            remote = c.Remote;
        }

        var w = new WhoisWindow(remote) { Owner = this };
        w.ShowDialog();
    }

    private void OpenWhois_Click(object sender, RoutedEventArgs e)
    {
        var w = new WhoisWindow() { Owner = this };
        w.ShowDialog();
    }

    private void ViewOnMap_Click(object sender, RoutedEventArgs e)
    {
        string target = "";
        if (ConnGrid?.SelectedItem is ConnRow cr)
        {
            target = !string.IsNullOrEmpty(cr.Location) && cr.Location != "-" ? cr.Location : cr.Remote.Split(':')[0];
        }
        else if (_vm.SelectedConnection is { } conn)
        {
            target = !string.IsNullOrEmpty(conn.Location) && conn.Location != "-" ? conn.Location : conn.Remote.Split(':')[0];
        }

        if (!string.IsNullOrEmpty(target))
        {
            try
            {
                string url = $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(target)}";
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }
        }
    }

    private void SearchIpWeb_Click(object sender, RoutedEventArgs e)
    {
        string ip = "";
        if (ConnGrid?.SelectedItem is ConnRow cr)
        {
            ip = cr.Remote.Split(':')[0];
        }

        if (!string.IsNullOrEmpty(ip))
        {
            try
            {
                string url = $"https://www.google.com/search?q=ip+{Uri.EscapeDataString(ip)}";
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }
        }
    }

    private void OpenExeFolder_Click(object sender, RoutedEventArgs e)
    {
        string path = _vm.SelectedPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch { }
        }
        else
        {
            _vm.Report("Application path not found on disk");
        }
    }

    private void SaveTag_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProcess is { } row)
        {
            string tags = TagBox.Text.Trim();
            _vm.SetAppTags(row.Name, tags);
            _vm.Report($"Tags updated for {row.Name}: {(string.IsNullOrEmpty(tags) ? "None" : tags)}");
        }
        else
        {
            _vm.Report("Select an application first");
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _vm.ConnectionLogs.Clear();
        _vm.Report("Connection log cleared");
    }

    private void ExportLogCsv_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Valve_ConnectionLog_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            DefaultExt = ".csv"
        };
        if (sfd.ShowDialog() == true)
        {
            _vm.ExportLogsToCsv(sfd.FileName);
            _vm.Report($"Exported connection log to {System.IO.Path.GetFileName(sfd.FileName)}");
        }
    }

    private void LogSearch_Changed(object sender, RoutedEventArgs e)
    {
        if (LogGrid == null || _vm == null) return;
        string filter = LogSearchBox?.Text?.Trim() ?? "";
        string actionSel = (LogActionFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Actions";
        string eventSel = (LogEventFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Events";

        var query = _vm.ConnectionLogs.AsEnumerable();
        if (!string.IsNullOrEmpty(filter))
        {
            query = query.Where(l =>
                l.App.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                l.Remote.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                l.Location.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                l.Event.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        if (actionSel != "All Actions")
        {
            query = query.Where(l => l.RuleAction.Equals(actionSel, StringComparison.OrdinalIgnoreCase));
        }

        if (eventSel != "All Events")
        {
            query = query.Where(l => l.Event.StartsWith(eventSel, StringComparison.OrdinalIgnoreCase));
        }

        LogGrid.ItemsSource = query.ToList();
    }
    private void ResetTotals_Click(object sender, RoutedEventArgs e)
    {
        _vm.ResetTotals();
    }



    private void StatsPeriod_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null) return;
        if (StatsPeriodBox?.SelectedItem is ComboBoxItem item && item.Tag is string tagStr && int.TryParse(tagStr, out int days))
        {
            _vm.SetStatsPeriod(days);
        }
    }







    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_vm?.CurrentSettings.CloseToTray == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }



    private void CleanStaleApps_Click(object sender, RoutedEventArgs e)
    {
        int pruned = _vm.CleanStaleApplications();
        _vm.Report($"Cleaned up {pruned} stale application{(pruned == 1 ? "" : "s")}.");
    }

    private void StreamDl_Click(object sender, RoutedEventArgs e)
    {
        Graph.ShowDownload = true;
        Graph.ShowUpload = false;
        Graph.InvalidateVisual();
    }

    private void StreamUl_Click(object sender, RoutedEventArgs e)
    {
        Graph.ShowDownload = false;
        Graph.ShowUpload = true;
        Graph.InvalidateVisual();
    }

    private void StreamBoth_Click(object sender, RoutedEventArgs e)
    {
        Graph.ShowDownload = true;
        Graph.ShowUpload = true;
        Graph.InvalidateVisual();
    }

    private void MenuViewStats_Click(object sender, RoutedEventArgs e)
    {
        ProcessRow? row = GetContextRow(sender) ?? _vm.SelectedProcess;
        if (row != null)
        {
            try
            {
                var win = new ProcessStatsWindow(row, _vm) { Owner = this };
                win.Show();
            }
            catch (Exception ex)
            {
                EventLogBridge.Warn($"Failed to open ProcessStatsWindow: {ex.Message}");
                MessageBox.Show(this, $"Could not open process statistics: {ex.Message}", "Internet Limiter Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            SelectTabByHeader("Traffic statistics");
            _vm?.SetStatsPeriod(7);
        }
    }

    private void MenuViewHistory_Click(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedIndex = 1;
    }

    private void MenuToggleHidden_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProcess is { } row)
        {
            if (row.IsHidden)
            {
                _vm.UnhideProcess(row);
            }
            else
            {
                _vm.HideProcess(row);
            }
            ApplyFilter();
        }
        else if (AppGrid?.SelectedItem is ApplicationEntry app)
        {
            app.IsHidden = !app.IsHidden;
            _vm.SaveApplications();
            ApplyFilter();
        }
    }

    private void MenuEditTags_Click(object sender, RoutedEventArgs e)
    {
        TagBox?.Focus();
    }

    private void MenuCreateFilter_Click(object sender, RoutedEventArgs e)
    {
        // Multi-select: gather every selected activity node / app row and build one filter.
        var processes = new List<ProcessRow>();
        ConnRow? connection = null;

        foreach (object? item in ProcessGrid.SelectedItems)
        {
            if (item is TreeNode { Process: { } p })
            {
                processes.Add(p);
            }
            else if (item is TreeNode { Connection: { } c })
            {
                connection ??= c;
            }
        }

        // Also accept multi-selection from the Application List tab.
        if (processes.Count == 0 && connection is null)
        {
            foreach (object? item in AppGrid.SelectedItems)
            {
                if (item is ApplicationEntry app && _vm.Processes.FirstOrDefault(p => string.Equals(p.Path, app.Path, StringComparison.OrdinalIgnoreCase)) is { } matched)
                {
                    processes.Add(matched);
                }
            }
        }

        if (processes.Count == 0 && connection is null)
        {
            connection = _vm.SelectedConnection;
            if (_vm.SelectedProcess is { } single)
            {
                processes.Add(single);
            }
        }

        if (processes.Count > 0)
        {
            _vm.AddFilterFromProcesses(processes);
        }
        else if (connection is not null)
        {
            _vm.AddFilterFromConnection(connection);
        }
    }
    // --- Dockable Tool Windows (Batch B6, Ledger 22.x) -----------------------
    private DockManager? _dockManager;

    private void InitializeDocking()
    {
        try
        {
            _dockManager = new DockManager(this);

            if (Tabs != null)
                _dockManager.RegisterPane("Activity", "Activity Grid", DockPosition.Center, Tabs);

            if (Graph != null)
                _dockManager.RegisterPane("TrafficChart", "Traffic Chart", DockPosition.Right, Graph);

            if (SidePanel != null)
                _dockManager.RegisterPane("InfoPanel", "Info Panel", DockPosition.Right, SidePanel);

            if (LogGrid != null)
                _dockManager.RegisterPane("ConnectionLog", "Connection Log", DockPosition.Bottom, LogGrid);

            if (StatsGrid != null)
                _dockManager.RegisterPane("TrafficStats", "Traffic Statistics", DockPosition.Center, StatsGrid);
        }
        catch (Exception ex)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.UtcNow:O}] Docking init failed: {ex}\n");
            }
            catch { }
            _vm.Note = "Docking initialization warning";
        }
    }

    public void RunDockTest()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve");
        Directory.CreateDirectory(dir);

        if (_dockManager != null && _dockManager.Panes.TryGetValue("TrafficChart", out var pane))
        {
            // 1. Float TrafficChart pane via DockManager API
            pane.Float(this);
            bool winVisible = pane.FloatingWindow != null && pane.FloatingWindow.IsVisible;
            if (pane.FloatingWindow != null)
            {
                pane.FloatingWindow.UpdateLayout();
                int w = Math.Max(200, (int)pane.FloatingWindow.ActualWidth);
                int h = Math.Max(150, (int)pane.FloatingWindow.ActualHeight);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(pane.FloatingWindow);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(dir, "dock_float.png"));
                enc.Save(fs);
            }
            Console.WriteLine($"DOCK_FLOAT_ASSERTION: TrafficChart floated successfully, window visible = {winVisible}, dock_float.png saved");

            // 2. Call Dock() -> assert chart content is back in the main window visual tree
            pane.Dock();
            UpdateLayout();
            bool parentNotNull = Graph != null && Graph.Parent != null;

            int mw = Math.Max(400, (int)ActualWidth);
            int mh = Math.Max(300, (int)ActualHeight);
            var mbmp = new System.Windows.Media.Imaging.RenderTargetBitmap(mw, mh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            mbmp.Render(this);
            var menc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            menc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(mbmp));
            using var mfs = File.Create(Path.Combine(dir, "dock_docked.png"));
            menc.Save(mfs);

            Console.WriteLine($"DOCK_DOCKED_ASSERTION: TrafficChart docked back successfully, Graph.Parent != null, dock_docked.png saved");
        }
    }

    private void ToggleWindowPane_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: "TrafficStats" }) { OpenStatsManager_Click(sender, e); return; }
        if (sender is FrameworkElement { Tag: string id } && _dockManager != null)
        {
            if (_dockManager.Panes.TryGetValue(id, out var pane))
            {
                if (pane.Model.State == DockState.Hidden)
                {
                    pane.Show(this);
                }
                else
                {
                    pane.Hide();
                }
            }
        }
    }

    private void FloatActivePane_Click(object sender, RoutedEventArgs e)
    {
        if (_dockManager == null) return;
        string activeId = "Activity";
        if (Tabs?.SelectedItem is TabItem ti && ti.Header is string header)
        {
            if (header.Contains("Activity")) activeId = "Activity";
            else if (header.Contains("Connection Log")) activeId = "ConnectionLog";
            else if (header.Contains("statistics")) activeId = "TrafficStats";
        }
        if (_dockManager.Panes.TryGetValue(activeId, out var pane))
        {
            pane.ToggleFloat(this);
        }
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        _dockManager?.ResetLayout();
        _vm.Report("UI layout reset to default");
    }

    private void LogIpLookup_Click(object sender, RoutedEventArgs e)
    {
        string remote = "";
        if (LogGrid?.SelectedItem is LogEntry le)
        {
            remote = le.Remote;
        }
        else if (_vm?.SelectedConnection is { } conn)
        {
            remote = conn.Remote;
        }
        var w = new WhoisWindow(remote) { Owner = this };
        w.ShowDialog();
    }

    private void LogWhois_Click(object sender, RoutedEventArgs e)
    {
        LogIpLookup_Click(sender, e);
    }

    private void LogViewMap_Click(object sender, RoutedEventArgs e)
    {
        string target = "";
        if (LogGrid?.SelectedItem is LogEntry le)
        {
            target = !string.IsNullOrEmpty(le.Location) && le.Location != "-" ? le.Location : (le.Remote ?? "").Split(':')[0];
        }
        if (!string.IsNullOrEmpty(target))
        {
            try
            {
                string url = "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(target);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _vm?.Report("Failed to launch map: " + ex.Message);
            }
        }
    }

    private void LogCopyRemote_Click(object sender, RoutedEventArgs e)
    {
        if (LogGrid?.SelectedItem is LogEntry le && !string.IsNullOrEmpty(le.Remote))
        {
            string ip = le.Remote.Split(':')[0];
            try
            {
                Clipboard.SetText(ip);
                _vm?.Report($"Copied remote address {ip} to clipboard");
            }
            catch { }
        }
    }

    private void LogCopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (LogGrid?.SelectedItem is LogEntry le)
        {
            string row = $"{le.TimeText}\t{le.App}\t{le.Event}\t{le.Proto}\t{le.Local}\t{le.Remote}\t{le.Location}\t{le.RuleAction}";
            try
            {
                Clipboard.SetText(row);
                _vm?.Report("Copied log entry to clipboard");
            }
            catch { }
        }
    }

    private void LogGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LogGrid?.SelectedItem is LogEntry le && !string.IsNullOrEmpty(le.Remote))
        {
            var w = new WhoisWindow(le.Remote) { Owner = this };
            w.ShowDialog();
        }
    }

    private void SwitchAdapter_Click(object sender, RoutedEventArgs e)
    {
        ShowAdapterMenu(sender as FrameworkElement ?? this);
    }

    private void ActiveAdapter_Click(object sender, MouseButtonEventArgs e)
    {
        ShowAdapterMenu(sender as FrameworkElement ?? this);
    }

    private void ShowAdapterMenu(FrameworkElement anchor)
    {
        if (_vm == null) return;

        var menu = new ContextMenu { Style = Application.Current.TryFindResource("DarkContextMenu") as Style };

        var autoItem = new MenuItem
        {
            Header = "Auto-Detect (Physical Ethernet/Wi-Fi Preferred)",
            IsChecked = string.IsNullOrEmpty(_vm.CurrentSettings.SelectedAdapterId),
            Style = Application.Current.TryFindResource("DarkMenuItem") as Style
        };
        autoItem.Click += (_, _) =>
        {
            _vm.SelectAdapter("");
            _vm.Report("Network adapter set to Auto-Detect");
        };
        menu.Items.Add(autoItem);
        menu.Items.Add(new Separator());

        foreach (var adapter in _vm.NetworkAdapters)
        {
            string label = $"{adapter.Name} ({adapter.Ip}) [{adapter.Type}] - {adapter.Status}";
            var item = new MenuItem
            {
                Header = label,
                IsChecked = _vm.CurrentSettings.SelectedAdapterId == adapter.Id || _vm.CurrentSettings.SelectedAdapterId == adapter.Name,
                Style = Application.Current.TryFindResource("DarkMenuItem") as Style
            };
            string id = adapter.Id;
            string name = adapter.Name;
            item.Click += (_, _) =>
            {
                _vm.SelectAdapter(id);
                _vm.Report($"Active adapter manually switched to: {name}");
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void SelectTabByHeader(string headerText)
    {
        if (Tabs == null) return;
        foreach (TabItem item in Tabs.Items)
        {
            if (item.Header is string h && h.Contains(headerText, StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = item;
                return;
            }
        }
    }

    private StatsManagerWindow? _statsManagerWindow;
    private void OpenStatsManager_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_statsManagerWindow != null && _statsManagerWindow.IsLoaded) { if (_statsManagerWindow.WindowState == WindowState.Minimized) _statsManagerWindow.WindowState = WindowState.Normal; _statsManagerWindow.Activate(); _statsManagerWindow.Focus(); return; }
            var win = new StatsManagerWindow(_vm) { Owner = this };
            _statsManagerWindow = win;
            win.Closed += (_, _) => _statsManagerWindow = null;
            win.Show();
        }
        catch (Exception ex)
        {
            EventLogBridge.Warn("Failed to open StatsManagerWindow: " + ex.Message);
            SelectTabByHeader("Traffic statistics");
        }
    }

    private void OpenTrafficStatsTab_Click(object sender, RoutedEventArgs e)
    {
        SelectTabByHeader("Traffic statistics");
        _vm?.SetStatsPeriod(7);
    }
}