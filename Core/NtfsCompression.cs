using System;
using System.Diagnostics;
using System.IO;

namespace Valve.Core
{
    /// <summary>NTFS compression toggle for the stats database folder (ledger 13.14.g).
    /// Uses the built-in compact.exe utility; compressing shrinks the DB roughly in half.</summary>
    public static class NtfsCompression
    {
        public static bool IsSupported => OperatingSystem.IsWindows();

        /// <summary>Compresses (true) or decompresses (false) every file under the folder recursively.</summary>
        public static string Toggle(string folder, bool compress)
        {
            if (!Directory.Exists(folder)) return "folder not found: " + folder;
            try
            {
                var psi = new ProcessStartInfo("compact.exe", $"{(compress ? "/c" : "/u")} /s:{folder} /q")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "compact.exe could not be started";
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(30000);
                return p.ExitCode == 0 ? $"{(compress ? "compressed" : "decompressed")} db folder" : "compact.exe exited " + p.ExitCode + ": " + outp.Trim();
            }
            catch (Exception ex) { return "NTFS compression failed: " + ex.Message; }
        }
    }
}
