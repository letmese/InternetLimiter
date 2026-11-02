using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace InternetLimiter.Installer;

public partial class MainWindow : Window
{
    private readonly InstallerEngine _engine = new();
    private bool _isCompleted = false;

    public MainWindow()
    {
        InitializeComponent();
        string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Internet Limiter");
        TxtInstallPath.Text = defaultPath;
        UpdateDiskSpace(defaultPath);

        TxtInstallPath.TextChanged += (_, _) => UpdateDiskSpace(TxtInstallPath.Text);
    }

    private void UpdateDiskSpace(string path)
    {
        try
        {
            string root = Path.GetPathRoot(path) ?? "C:\\";
            var drive = new DriveInfo(root);
            long freeMb = drive.AvailableFreeSpace / (1024 * 1024);
            TxtDiskSpace.Text = $"Space required: ~65 MB · Space available on {drive.Name.TrimEnd('\\')}: {freeMb:N0} MB";
        }
        catch
        {
            TxtDiskSpace.Text = "Space required: ~65 MB";
        }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Installation Directory for Internet Limiter",
            UseDescriptionForTitle = true,
            InitialDirectory = TxtInstallPath.Text
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtInstallPath.Text = dialog.SelectedPath;
        }
    }

    private async void BtnAction_Click(object sender, RoutedEventArgs e)
    {
        if (_isCompleted)
        {
            // Finish clicked
            if (ChkLaunchApp.IsChecked == true)
            {
                try
                {
                    string targetExe = Path.Combine(TxtInstallPath.Text, "InternetLimiter.exe");
                    if (!File.Exists(targetExe)) targetExe = Path.Combine(TxtInstallPath.Text, "Valve.exe");

                    if (File.Exists(targetExe))
                    {
                        Process.Start(new ProcessStartInfo(targetExe) { UseShellExecute = true });
                    }
                }
                catch { }
            }

            Close();
            return;
        }

        // Start installation
        string targetDir = TxtInstallPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show("Please specify a valid installation directory.", "Internet Limiter Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool installService = ChkService.IsChecked == true;
        bool createShortcuts = (ChkDesktopShortcut.IsChecked == true) || (ChkStartMenuShortcut.IsChecked == true);

        // Switch to progress view
        ConfigPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        BtnAction.IsEnabled = false;
        BtnAction.Content = "Installing...";
        BtnCancel.IsEnabled = false;

        try
        {
            await Task.Run(() =>
            {
                _engine.PerformInstall(targetDir, installService, createShortcuts, (message, percent) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        TxtStatus.Text = message;
                        TxtDetail.Text = $"{percent}% completed";
                        PrgBar.Value = percent;
                        TxtLog.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
                        ScrollLog.ScrollToBottom();
                    });
                });
            });

            _isCompleted = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            SuccessPanel.Visibility = Visibility.Visible;
            BtnCancel.Visibility = Visibility.Collapsed;
            BtnAction.IsEnabled = true;
            BtnAction.Content = "Finish";
            TxtFooterInfo.Text = "Internet Limiter installed and ready";
        }
        catch (Exception ex)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ConfigPanel.Visibility = Visibility.Visible;
            BtnAction.IsEnabled = true;
            BtnAction.Content = "Try Again";
            BtnCancel.IsEnabled = true;

            MessageBox.Show(
                $"Installation encountered an error:\n\n{ex.Message}",
                "Internet Limiter Setup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
