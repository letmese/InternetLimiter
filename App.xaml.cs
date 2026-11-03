using System.IO.Compression;
using System.IO;
using System.Windows;
using Valve.Core;

namespace Valve;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _userDeclinedUac;
    private static System.Threading.Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Some GPU driver / multi-monitor combinations hand WPF a blank white
        // surface for a dense window like this, even though the visual tree is
        // fine (proven by rendering it offscreen). Software rendering costs
        // almost nothing for a data grid and removes that whole failure mode.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        if (e.Args.Length > 0)
        {
            Valve.Interop.Native.AttachConsole(Valve.Interop.Native.ATTACH_PARENT_PROCESS);
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            MessageBox.Show(
                $"Internet Limiter hit an unexpected error.\n\n{args.Exception.Message}",
                "Internet Limiter", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // 1. Command-line dispatchers
        if (e.Args.Contains("--probe-service"))
        {
            string? status = Valve.Core.ValveIpcClient.SendCommand("STATUS");
            Console.WriteLine(status ?? "{\"status\":\"unavailable\",\"shaperAvailable\":false,\"activeApps\":0}");
            Shutdown(0);
            return;
        }

        if (e.Args.Contains("--service"))
        {
            try
            {
                Valve.Core.WindowsServiceDispatcher.RunAsServiceOrInteractive();
            }
            catch (Exception ex)
            {
                Log(ex);
            }
            Shutdown(0);
            return;
        }

        if (e.Args.Contains("--install-service"))
        {
            var res = Valve.Core.ServiceManager.InstallService();
            Console.WriteLine(res.Message);
            Shutdown(res.Success ? 0 : 1);
            return;
        }

        if (e.Args.Contains("--uninstall-service"))
        {
            var res = Valve.Core.ServiceManager.UninstallService();
            Console.WriteLine(res.Message);
            Shutdown(res.Success ? 0 : 1);
            return;
        }

        if (e.Args.Contains("--uninstall"))
        {
            ExecuteUninstall(e.Args.Contains("--quiet"));
            Shutdown(0);
            return;
        }

        // Single UI instance check
        bool isTestOrScreenshot = e.Args.Contains("--test-dialogs") || e.Args.Contains("--test-dock") || e.Args.Contains("--capture-screenshots");
        if (!isTestOrScreenshot)
        {
            _singleInstanceMutex = new System.Threading.Mutex(true, @"Local\InternetLimiter_SingleInstance_UI_Mutex", out bool createdNew);
            if (!createdNew)
            {
                IntPtr existingHwnd = Valve.Interop.Native.FindWindow(null, "Internet Limiter");
                if (existingHwnd == IntPtr.Zero) existingHwnd = Valve.Interop.Native.FindWindow(null, "Valve");
                if (existingHwnd != IntPtr.Zero)
                {
                    Valve.Interop.Native.ShowWindow(existingHwnd, Valve.Interop.Native.SW_RESTORE);
                    Valve.Interop.Native.SetForegroundWindow(existingHwnd);
                }
                Shutdown(0);
                return;
            }
        }

        // 2. Client-Service fast startup: if service is running, launch GUI instantly without UAC prompt!
        bool isStartup = e.Args.Contains("--startup");
        bool serviceRunning = false;
        if (isStartup)
        {
            for (int i = 0; i < 12; i++)
            {
                if (Valve.Core.ValveIpcClient.IsServiceRunning(250))
                {
                    serviceRunning = true;
                    break;
                }
                System.Threading.Thread.Sleep(250);
            }
        }
        else
        {
            serviceRunning = Valve.Core.ValveIpcClient.IsServiceRunning();
        }

        if (serviceRunning)
        {
            try
            {
                BuildInterface(e);
                return;
            }
            catch (Exception ex)
            {
                Log(ex);
                MessageBox.Show("Internet Limiter could not start its interface.\n\n" + ex.Message, "Internet Limiter", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }

        // 3. Fallback standalone path: if service not running, request UAC elevation for local kernel driver
        if (!Valve.Interop.Native.IsUserAnAdmin() && !e.Args.Contains("--test-dialogs") && !e.Args.Contains("--test-dock") && !e.Args.Contains("--capture-screenshots"))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? "Valve.exe",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                foreach (string arg in e.Args) psi.ArgumentList.Add(arg);
                System.Diagnostics.Process.Start(psi);
                Shutdown(0);
                return;
            }
            catch
            {
                _userDeclinedUac = true;
            }
        }

        try
        {
            BuildInterface(e);
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show(
                "Internet Limiter could not start its interface.\n\n"
                + ex.Message
                + "\n\nDetails were written to %LOCALAPPDATA%\\Valve\\error.log",
                "Internet Limiter", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void BuildInterface(StartupEventArgs e)
    {
        var collector = new Collector();
        IShaper shaper = new QosShaper();
        IFirewall firewall = new WfpFirewall();
        var history = new HistoryStore();
        var store = new SettingsStore();
        var geo = new GeoIp();

        _viewModel = new MainViewModel(collector, shaper, firewall, history, store, geo);

        var window = new MainWindow(_viewModel);
        MainWindow = window;

        SetUpTray(window);

        // Minimising sends Valve to the tray instead of the taskbar, so a monitor
        // that is meant to run all day does not occupy a window slot.
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized
                && _viewModel?.CurrentSettings.MinimizeToTray == true)
            {
                window.Hide();
            }
        };

        bool startMinimized = e.Args.Contains("--startup") || _viewModel.CurrentSettings.StartMinimized;
        if (startMinimized)
        {
            window.WindowState = WindowState.Minimized;
            if (_viewModel.CurrentSettings.MinimizeToTray)
            {
                var _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                window.Hide();
            }
            else
            {
                window.Show();
            }
        }
        else
        {
            window.Show();
        }

        _viewModel.GraphUpdated += () =>
        {
            if (_tray is not null && _viewModel is not null)
            {
                string tip = $"Valve · DL: {_viewModel.TotalInText} · UL: {_viewModel.TotalOutText}";
                if (tip.Length > 63) tip = tip.Substring(0, 63);
                _tray.Text = tip;
            }
        };

        _viewModel.Start();

        // Startup housekeeping (ledger 27.01 / 28.07 / 28.12 / 28.17): localization,
        // event log bridge, background prerequisite + update checks.
        Valve.Core.Localization.Init();
        EventLogBridge.Init();
        EventLogBridge.Info("Valve started (v" + Valve.Api.ValveApi.Version + ")");

        System.Threading.Tasks.Task.Run(() =>
        {
            var missing = PrereqChecker.FindMissing();
            if (missing.Count == 0) return;
            var missingText = string.Join(", ", missing);
            EventLogBridge.Warn("Missing prerequisites: " + missingText);
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show("Valve detected missing prerequisites:\n\n" + missingText +
                    "\n\nInstall them (e.g. .NET 8 Desktop Runtime, VC++ Redistributable) and restart Valve.",
                    "Valve prerequisites", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        });

        System.Threading.Tasks.Task.Run(async () =>
        {
            var up = await VersionChecker.CheckAsync();
            if (!up.update || _tray is null) return;
            _tray.ShowBalloonTip(5000, "Valve Update", "Update available: v" + up.latest,
                System.Windows.Forms.ToolTipIcon.Info);
        });

        if (e.Args.Contains("--test-dialogs"))
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3),
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var row = _viewModel.Processes.FirstOrDefault() ?? new ProcessRow(4, "TestApp", "C:////test.exe");
                    var dialog = new RuleEditorWindow(_viewModel, row) { Owner = window };
                    dialog.Show();

                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        Math.Max(100, (int)dialog.ActualWidth), Math.Max(100, (int)dialog.ActualHeight), 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(dialog);

                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve");
                    Directory.CreateDirectory(dir);
                    using FileStream stream = File.Create(Path.Combine(dir, "dialog.png"));
                    encoder.Save(stream);
                    Console.WriteLine("screenshot = saved");

                    window.RunDockTest();
                }
                catch (Exception ex)
                {
                    Log(ex);
                }

                Shutdown();
            };

            timer.Start();
        }

        if (e.Args.Contains("--test-dock"))
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2),
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    window.RunDockTest();
                }
                catch (Exception ex)
                {
                    Log(ex);
                }
                Shutdown();
            };

            timer.Start();
        }

        if (e.Args.Contains("--capture-screenshots"))
        {
            CapturePrivacyScreenshots(window);
        }
        if (e.Args.Contains("--diag") || e.Args.Contains("--diag-zip"))
        {
            RunDiagnostics(window, e.Args.Contains("--diag-zip"));
        }

        if (_viewModel.CurrentSettings.StartMinimized)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void RunDiagnostics(Window window, bool createZip = false)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(9),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var report = new System.Text.StringBuilder();
            if (_userDeclinedUac || !Valve.Interop.Native.IsUserAnAdmin())
            {
                report.AppendLine("WARNING: non-elevated — WinDivert cannot open; results below show monitoring fallback only");
            }
            try
            {
                report.AppendLine($"window.loaded   = {window.IsLoaded}");
                report.AppendLine($"window.size     = {window.ActualWidth}x{window.ActualHeight}");
                report.AppendLine($"window.visible  = {window.IsVisible}");
                report.AppendLine($"window.bg       = {window.Background}");
                report.AppendLine($"window.content  = {window.Content?.GetType().Name ?? "null"}");
                report.AppendLine($"merged dicts    = {Application.Current.Resources.MergedDictionaries.Count}");
                report.AppendLine($"Bg resource     = {Application.Current.TryFindResource("Bg") ?? "MISSING"}");
                report.AppendLine($"Panel resource  = {Application.Current.TryFindResource("Panel") ?? "MISSING"}");
                report.AppendLine($"TitleText style = {Application.Current.TryFindResource("TitleText") ?? "MISSING"}");

                if (window.Content is FrameworkElement root)
                {
                    report.AppendLine($"root type       = {root.GetType().Name}");
                    report.AppendLine($"root size       = {root.ActualWidth}x{root.ActualHeight}");
                    report.AppendLine($"root children   = {System.Windows.Media.VisualTreeHelper.GetChildrenCount(root)}");
                    report.AppendLine($"root visible    = {root.IsVisible}");
                }

                report.AppendLine($"vm tree rows    = {_viewModel?.TreeRows.Count}");
                report.AppendLine($"vm processes    = {_viewModel?.Processes.Count}");
                report.AppendLine($"divert avail    = {_viewModel?.DivertAvailable}");
                report.AppendLine($"divert_reason   = {_viewModel?.DivertUnavailableReason}");
                report.AppendLine($"shaper avail    = {_viewModel?.ShaperAvailable}");
                report.AppendLine($"service_shaper_available = {_viewModel?.ServiceShaperAvailable}");
                report.AppendLine($"service_heartbeat_age_ms = {_viewModel?.ServiceHeartbeatAgeMs:F0}");
                report.AppendLine($"elevated        = {Valve.Interop.Native.IsUserAnAdmin()}");
                report.AppendLine($"divert_available= {_viewModel?.DivertAvailable}");
                report.AppendLine($"stats_source    = {(_viewModel?.IsConnectedToService == true ? "service" : (_viewModel?.DivertAvailable == true ? "divert" : "local"))}");
                report.AppendLine($"engine_heartbeat_age_ms = {_viewModel?.EngineHeartbeatAgeMs:F0}");
                report.AppendLine($"pid0_ratio      = {_viewModel?.Pid0Ratio:P1}");
                report.AppendLine($"trygetstats_hits= {_viewModel?.TryGetStatsHitRatio:P1}");
                string rateStatus = ((_viewModel?.TotalIn ?? 0) > 0 || (_viewModel?.TotalOut ?? 0) > 0) ? "live" : "idle";
                report.AppendLine($"rates={rateStatus} pid0_ratio={_viewModel?.Pid0Ratio:P1} totalIn={_viewModel?.TotalInText} totalOut={_viewModel?.TotalOutText}");
                report.AppendLine($"prereq missing  = {(Valve.Core.PrereqChecker.FindMissing().Count == 0 ? "NONE (OK)" : string.Join("; ", Valve.Core.PrereqChecker.FindMissing()))}");

                try
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Max(1, window.ActualWidth),
                        (int)Math.Max(1, window.ActualHeight),
                        96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);

                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

                    string shotDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve");
                    Directory.CreateDirectory(shotDir);
                    using FileStream stream = File.Create(Path.Combine(shotDir, "shot.png"));
                    encoder.Save(stream);

                    report.AppendLine($"screenshot      = saved");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"screenshot      = FAILED {ex.GetType().Name}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"DIAG FAILED: {ex}");
            }

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "diag.txt"), report.ToString());
                if (createZip)
                {
                    try
                    {
                        string zipPath = Path.Combine(dir, $"ValveDiag_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
                        if (File.Exists(zipPath)) File.Delete(zipPath);
                        using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                        {
                            string diagFile = Path.Combine(dir, "diag.txt");
                            if (File.Exists(diagFile)) archive.CreateEntryFromFile(diagFile, "diag.txt");
                            string errFile = Path.Combine(dir, "error.log");
                            if (File.Exists(errFile)) archive.CreateEntryFromFile(errFile, "error.log");
                            string setFile = Path.Combine(dir, "settings.json");
                            if (File.Exists(setFile)) archive.CreateEntryFromFile(setFile, "settings.json");
                        }
                    }
                    catch (Exception zex)
                    {
                        Log(zex);
                    }
                }
            }
            catch
            {
                // nothing useful to do here
            }

            Shutdown();
        };

        timer.Start();
    }

    private void SetUpTray(Window window)
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "Internet Limiter - Bandwidth & Firewall",
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();

        var itemBlocker = new System.Windows.Forms.ToolStripMenuItem("Blocker: ON");
        itemBlocker.Click += (_, _) =>
        {
            if (_viewModel != null)
            {
                _viewModel.CurrentSettings.BlockerEnabled = !_viewModel.CurrentSettings.BlockerEnabled;
                _viewModel.ApplySettings();
            }
        };

        var itemLimiter = new System.Windows.Forms.ToolStripMenuItem("Limiter: ON");
        itemLimiter.Click += (_, _) =>
        {
            if (_viewModel != null)
            {
                _viewModel.CurrentSettings.LimiterEnabled = !_viewModel.CurrentSettings.LimiterEnabled;
                _viewModel.ApplySettings();
            }
        };

        var itemEmergency = new System.Windows.Forms.ToolStripMenuItem("Emergency Block: OFF");
        itemEmergency.Click += (_, _) =>
        {
            if (_viewModel != null)
            {
                _viewModel.EmergencyBlockActive = !_viewModel.EmergencyBlockActive;
            }
        };

        menu.Opening += (_, _) =>
        {
            if (_viewModel != null)
            {
                itemBlocker.Text = _viewModel.CurrentSettings.BlockerEnabled ? "Blocker: ON" : "Blocker: OFF";
                itemBlocker.Checked = _viewModel.CurrentSettings.BlockerEnabled;

                itemLimiter.Text = _viewModel.CurrentSettings.LimiterEnabled ? "Limiter: ON" : "Limiter: OFF";
                itemLimiter.Checked = _viewModel.CurrentSettings.LimiterEnabled;

                itemEmergency.Text = _viewModel.EmergencyBlockActive ? "EMERGENCY BLOCK: ACTIVE" : "Emergency Block: OFF";
                itemEmergency.Checked = _viewModel.EmergencyBlockActive;
            }
        };

        menu.Items.Add(itemBlocker);
        menu.Items.Add(itemLimiter);
        menu.Items.Add(itemEmergency);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Open Internet Limiter", null, (_, _) => Restore(window));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Restore(window);
    }
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(iconPath)) return new System.Drawing.Icon(iconPath);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private static void Restore(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _viewModel?.Dispose();
        base.OnExit(e);
    }


    private static void ExecuteUninstall(bool quiet)
    {
        if (!quiet)
        {
            var confirm = MessageBox.Show(
                "Are you sure you want to completely uninstall Internet Limiter and remove its background service?",
                "Internet Limiter Uninstall",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        try
        {
            // 1. Stop and remove the service
            Valve.Core.ServiceManager.UninstallService();

            // 2. Remove shortcuts
            Valve.Core.ServiceManager.RemoveShortcuts();

            // 3. Unregister from Add/Remove Programs
            Valve.Core.ServiceManager.UnregisterUninstall();

            // 4. Schedule directory removal script
            string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            if (appDir.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
            {
                string cmd = $"/c timeout /t 2 /nobreak > nul & rmdir /s /q \"{appDir}\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", cmd)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }

            if (!quiet)
            {
                MessageBox.Show(
                    "Internet Limiter has been successfully uninstalled.",
                    "Internet Limiter Uninstall",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show("Uninstall error: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private static void Log(Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valve");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // logging must never mask the original failure
        }
    }

    private void CapturePrivacyScreenshots(MainWindow window)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                string docsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs", "screenshots");
                Directory.CreateDirectory(docsDir);

                // 1. Capture Main Window
                window.Width = 1100;
                window.Height = 720;
                window.UpdateLayout();
                SaveWindowBitmap(window, Path.Combine(docsDir, "01_main_window.png"));

                // 2. Capture Rule Editor
                var row = new ProcessRow(1420, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe")
                {
                    DownloadLimitKbps = 2048,
                    LimitKbps = 512,
                    Priority = TrafficPriority.High
                };
                var ruleWin = new RuleEditorWindow(_viewModel!, row) { Owner = window };
                ruleWin.Show();
                ruleWin.UpdateLayout();
                SaveWindowBitmap(ruleWin, Path.Combine(docsDir, "02_rule_editor.png"));
                ruleWin.Close();

                // 3. Capture Stats Manager Window
                var statsWin = new StatsManagerWindow(_viewModel!) { Owner = window };
                statsWin.Show();
                statsWin.UpdateLayout();
                SaveWindowBitmap(statsWin, Path.Combine(docsDir, "03_stats_manager.png"));
                statsWin.Close();

                // 4. Capture Settings Window
                var settingsWin = new SettingsWindow(_viewModel!) { Owner = window };
                settingsWin.Show();
                settingsWin.UpdateLayout();
                SaveWindowBitmap(settingsWin, Path.Combine(docsDir, "04_settings.png"));
                settingsWin.Close();

                Console.WriteLine("Screenshots captured successfully to " + docsDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Screenshot error: " + ex);
            }
            Shutdown(0);
        };
        timer.Start();
    }

    private static void SaveWindowBitmap(Window win, string filePath)
    {
        int width = (int)Math.Max(100, win.ActualWidth > 0 ? win.ActualWidth : win.Width);
        int height = (int)Math.Max(100, win.ActualHeight > 0 ? win.ActualHeight : win.Height);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(win);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }
}