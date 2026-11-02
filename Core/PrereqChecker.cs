using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Valve.Core
{
    /// <summary>Prerequisite detection for .NET Desktop Runtime, VC++ Redistributable and WinDivert (ledger 28.07).</summary>
    public static class PrereqChecker
    {
        public static List<string> FindMissing()
        {
            var missing = new List<string>();
            if (!DesktopRuntimePresent())
                missing.Add(".NET 8 Desktop Runtime (x64)");
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"))
            {
                if (key == null || (Convert.ToInt32(key.GetValue("Installed", 0)) == 0))
                    missing.Add("Microsoft Visual C++ 2015-2022 Redistributable (x64)");
            }
            string baseDir = AppContext.BaseDirectory;
            bool hasWinDivert = File.Exists(Path.Combine(baseDir, "WinDivert.dll")) || File.Exists(Path.Combine(baseDir, "x64", "WinDivert.dll"));
            if (!hasWinDivert) missing.Add("WinDivert.dll (driver library)");
            return missing;
        }

        /// <summary>
        /// Reliable .NET Desktop Runtime detection. The registry hive
        /// (InstalledVersions) is only written by the bundled installer, while
        /// dotnet-install / VS deployments only materialize the shared folder.
        /// Check both so we never report a false positive.
        /// </summary>
        private static bool DesktopRuntimePresent()
        {
            try
            {
                string shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(shared) && Directory.GetDirectories(shared).Length > 0)
                    return true;
            }
            catch { /* fall through to registry probe */ }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
                return key != null;
            }
            catch { return false; }
        }
    }
}
