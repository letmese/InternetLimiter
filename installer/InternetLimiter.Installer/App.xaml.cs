using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace InternetLimiter.Installer;

public partial class App : Application
{
    private void App_Startup(object sender, StartupEventArgs e)
    {
        // Support silent installation via CLI flags: /S, /silent, /quiet, --silent
        bool isSilent = e.Args.Any(a => 
            a.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        if (isSilent)
        {
            try
            {
                string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Internet Limiter");
                // Check if custom target dir was provided via /D=... or --dir=...
                foreach (string arg in e.Args)
                {
                    if (arg.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
                        targetDir = arg.Substring(3).Trim('"');
                    else if (arg.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
                        targetDir = arg.Substring(6).Trim('"');
                }

                bool installService = !e.Args.Contains("--no-service");
                bool createShortcuts = !e.Args.Contains("--no-shortcuts");
                bool startWithWindows = !e.Args.Contains("--no-startup");

                var engine = new InstallerEngine();
                engine.PerformInstall(targetDir, installService, createShortcuts, startWithWindows, null);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "internet_limiter_install_error.log"), ex.ToString());
                Environment.Exit(1);
            }
            return;
        }

        var mainWin = new MainWindow();
        mainWin.Show();
    }
}
