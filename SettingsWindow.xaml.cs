using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Valve.Core;

namespace Valve;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        VersionHeader.Text = "v" + Valve.Api.ValveApi.Version;
        AboutVersionText.Text = $"{Valve.Api.ValveApi.Version} (x64 Native / WinDivert / WFP)";
        Load(_vm.CurrentSettings);
    }

    private void Load(Settings settings)
    {
        // 1. Sampling Interval
        foreach (object item in IntervalBox.Items)
        {
            if (item is ComboBoxItem { Tag: string tag }
                && double.TryParse(tag, out double seconds)
                && Math.Abs(seconds - settings.SampleIntervalSeconds) < 0.01)
            {
                IntervalBox.SelectedItem = item;
                break;
            }
        }
        if (IntervalBox.SelectedItem is null)
        {
            IntervalBox.SelectedIndex = 1;
        }

        // 2. Units
        UnitsBits.IsChecked = settings.UseBits;
        UnitsBytes.IsChecked = !settings.UseBits;

        // 3. Windows Integration & Behavior
        StartWithWindows.IsChecked = settings.StartWithWindows;
        StartMinimized.IsChecked = settings.StartMinimized;
        MinimizeToTray.IsChecked = settings.MinimizeToTray;
        CloseToTray.IsChecked = settings.CloseToTray;
        ConfirmOnExit.IsChecked = settings.ConfirmOnExit;
        ResolveLocations.IsChecked = settings.ResolveLocations;

        // 4. Firewall & Blocker
        BlockerEnabled.IsChecked = settings.BlockerEnabled;
        EmergencyBlockAll.IsChecked = settings.EmergencyBlockAllTraffic;
        TempBlockMinutesBox.Text = settings.TemporaryBlockMinutes.ToString();
        string defaultAction = settings.DefaultBlockerAction ?? "Ask";
        foreach (object item in DefaultBlockerActionBox.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag.Equals(defaultAction, StringComparison.OrdinalIgnoreCase))
            {
                DefaultBlockerActionBox.SelectedItem = item;
                break;
            }
        }
        if (DefaultBlockerActionBox.SelectedItem is null)
        {
            DefaultBlockerActionBox.SelectedIndex = 0;
        }

        // 5. History Retention
        int retention = settings.HistoryRetentionDays;
        foreach (object item in RetentionBox.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int days) && days == retention)
            {
                RetentionBox.SelectedItem = item;
                break;
            }
        }
        if (RetentionBox.SelectedItem is null)
        {
            RetentionBox.SelectedIndex = 0;
        }

        // 6. DB Stats & Path
        var stats = _vm.GetHistoryStats();
        string formattedBytes = Fmt.Bytes(stats.TotalBytes);
        DbStatsText.Text = $"{stats.DayCount} daily records saved ({formattedBytes} on disk)";
        DataPathText.Text = _vm.DataFolder;

        // 7. Theme
        string theme = settings.Theme ?? "Dark";
        foreach (object item in ThemeBox.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag.Equals(theme, StringComparison.OrdinalIgnoreCase))
            {
                ThemeBox.SelectedItem = item;
                break;
            }
        }
        if (ThemeBox.SelectedItem is null)
        {
            ThemeBox.SelectedIndex = 0;
        }

        // 8. Security (13.x, 15.x)
        VtKeyBox.Text = settings.VirusTotalApiKey ?? "";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Settings settings = _vm.CurrentSettings;

        // Telemetry interval
        if (IntervalBox.SelectedItem is ComboBoxItem { Tag: string tagInterval }
            && double.TryParse(tagInterval, out double seconds))
        {
            settings.SampleIntervalSeconds = seconds;
        }

        // Units
        settings.UseBits = UnitsBits.IsChecked == true;

        // Integration
        settings.StartWithWindows = StartWithWindows.IsChecked == true;
        settings.StartMinimized = StartMinimized.IsChecked == true;
        settings.MinimizeToTray = MinimizeToTray.IsChecked == true;
        settings.CloseToTray = CloseToTray.IsChecked == true;
        settings.ConfirmOnExit = ConfirmOnExit.IsChecked == true;
        settings.ResolveLocations = ResolveLocations.IsChecked == true;

        // Blocker
        settings.BlockerEnabled = BlockerEnabled.IsChecked == true;
        settings.EmergencyBlockAllTraffic = EmergencyBlockAll.IsChecked == true;
        if (DefaultBlockerActionBox.SelectedItem is ComboBoxItem { Tag: string tagAction })
        {
            settings.DefaultBlockerAction = tagAction;
        }

        // History Retention
        if (RetentionBox.SelectedItem is ComboBoxItem { Tag: string tagRetention } && int.TryParse(tagRetention, out int days))
        {
            settings.HistoryRetentionDays = days;
        }

        // DB size warning limit (13.15) + month start day (26.06) + temporary duration (3.04)
        if (int.TryParse(DbSizeLimitBox.Text.Trim(), out int sizeMb) && sizeMb >= 1)
        {
            settings.StatsDbSizeLimitMB = sizeMb;
        }
        if (int.TryParse(MonthStartDayBox.Text.Trim(), out int monthDay) && monthDay >= 1 && monthDay <= 28)
        {
            settings.FirstDayOfMonth = monthDay;
        }
        if (int.TryParse(TempBlockMinutesBox.Text.Trim(), out int tempMinutes) && tempMinutes >= 1)
        {
            settings.TemporaryBlockMinutes = tempMinutes;
        }

        // Theme
        if (ThemeBox.SelectedItem is ComboBoxItem { Tag: string tagTheme })
        {
            if (!string.Equals(settings.Theme, tagTheme, StringComparison.OrdinalIgnoreCase))
            {
                settings.Theme = tagTheme;
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    mw.ApplyTheme(tagTheme);
                }
            }
        }

        // Security (13.x, 15.x)
        settings.VirusTotalApiKey = (VtKeyBox.Text ?? "").Trim();

        _vm.ApplySettings();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Permissions_Click(object sender, RoutedEventArgs e)
    {
        // 15.x: manage remote command permissions (persisted with settings on Save)
        var win = new PermissionsWindow(_vm) { Owner = this };
        win.ShowDialog();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _vm.DataFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Valve", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string historyFolder = Path.Combine(_vm.DataFolder, "history");
            Directory.CreateDirectory(historyFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = historyFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Valve", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
