using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace InternetLimiter.Installer;

public class InstallerEngine
{
    public const string ServiceName = "InternetLimiterService";
    public const string DisplayName = "Internet Limiter Service";
    public const string UninstallRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\InternetLimiter";

    public void PerformInstall(string targetDir, bool installService, bool createShortcuts, bool startWithWindows = true, Action<string, int>? progressCallback = null)
    {
        Report(progressCallback, "Preparing installation environment...", 5);

        // 1. Terminate running application or service instances to avoid file-in-use locks
        Report(progressCallback, "Stopping existing processes and services...", 15);
        StopExistingProcessesAndService();

        // 2. Prepare destination directory
        Report(progressCallback, $"Creating destination directory: {targetDir}", 25);
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        // 3. Extract payload files
        Report(progressCallback, "Extracting application binaries and drivers...", 40);
        ExtractPayload(targetDir);

        // Ensure Valve.exe exists alongside InternetLimiter.exe
        string mainExe = Path.Combine(targetDir, "InternetLimiter.exe");
        string valveExe = Path.Combine(targetDir, "Valve.exe");
        if (File.Exists(mainExe) && !File.Exists(valveExe))
        {
            try { File.Copy(mainExe, valveExe, true); } catch { }
        }
        else if (File.Exists(valveExe) && !File.Exists(mainExe))
        {
            try { File.Copy(valveExe, mainExe, true); } catch { }
        }

        // Ensure Assets directory exists in target
        string targetAssets = Path.Combine(targetDir, "Assets");
        if (!Directory.Exists(targetAssets)) Directory.CreateDirectory(targetAssets);

        // 4. Register and configure Windows Service
        if (installService)
        {
            Report(progressCallback, "Configuring Internet Limiter Windows Service...", 65);
            ConfigureWindowsService(mainExe);
        }

        // 5. Create shortcuts
        if (createShortcuts)
        {
            Report(progressCallback, "Creating Desktop and Start Menu shortcuts...", 80);
            CreateShortcuts(mainExe, targetDir);
        }

        // 5b. Configure automatic Windows startup
        if (startWithWindows)
        {
            Report(progressCallback, "Configuring automatic startup on Windows boot...", 90);
            ConfigureAutoStartup(mainExe, targetDir);
        }

        // 6. Register in Windows Add/Remove Programs
        Report(progressCallback, "Registering in Windows Installed Apps...", 95);
        RegisterUninstall(targetDir, mainExe);

        Report(progressCallback, "Installation completed successfully!", 100);
    }

    private void Report(Action<string, int>? callback, string message, int percent)
    {
        callback?.Invoke(message, percent);
    }

    private void StopExistingProcessesAndService()
    {
        try
        {
            // 1. Stop InternetLimiterService if running
            RunSc($"stop {ServiceName}");

            // 2. Stop WinDivert kernel driver services
            RunSc("stop WinDivert");
            RunSc("stop WinDivert14");
            RunSc("stop windivert");
            Thread.Sleep(300);

            // 3. Kill any running GUI, engine or related processes
            foreach (var procName in new[] { "InternetLimiter", "Valve", "InternetLimiterService", "ytgrid" })
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(2000);
                    }
                    catch { }
                }
            }

            // 4. Second attempt to stop driver after process handles are closed
            RunSc("stop WinDivert");
            RunSc("stop WinDivert14");
            Thread.Sleep(300);
        }
        catch { }
    }

    private void ExtractPayload(string targetDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("InternetLimiter.Installer.Payload.zip");

        if (stream != null)
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                string destinationPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                if (!destinationPath.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                    continue; // Path traversal protection

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                }
                else
                {
                    string? dir = Path.GetDirectoryName(destinationPath);
                    if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    try
                    {
                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }
                    catch (IOException) when (File.Exists(destinationPath))
                    {
                        // File is locked (e.g. WinDivert64.sys locked by kernel).
                        // Attempt stopping driver services and retry.
                        RunSc("stop WinDivert");
                        RunSc("stop WinDivert14");
                        Thread.Sleep(600);

                        try
                        {
                            entry.ExtractToFile(destinationPath, overwrite: true);
                        }
                        catch (IOException)
                        {
                            // If the existing file is valid on disk, keep it so setup succeeds gracefully
                            var fi = new FileInfo(destinationPath);
                            if (fi.Length == 0)
                            {
                                throw; // Rethrow only if target file on disk is empty
                            }
                        }
                    }
                }
            }
        }
        else
        {
            // Fallback for development/local builds: copy directly from publish folder if found
            string publishDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "publish");
            if (!Directory.Exists(publishDir))
            {
                publishDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "publish");
            }

            if (Directory.Exists(publishDir))
            {
                CopyDirectory(publishDir, targetDir);
            }
            else
            {
                throw new FileNotFoundException("Installer payload not found. Payload.zip must be embedded in the installer.");
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string dest = Path.Combine(destDir, Path.GetFileName(file));
            try
            {
                File.Copy(file, dest, true);
            }
            catch (IOException) when (File.Exists(dest))
            {
                var fi = new FileInfo(dest);
                if (fi.Length == 0) throw;
            }
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string dest = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, dest);
        }
    }

    private void ConfigureWindowsService(string exePath)
    {
        string binPath = $"\"{exePath}\" --service";

        try
        {
            // Try config first if service already exists
            var (cfgCode, _) = RunSc($"config {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"");
            if (cfgCode != 0)
            {
                // Stop and delete old service if stuck
                RunSc($"stop {ServiceName}");
                RunSc($"delete {ServiceName}");
                Thread.Sleep(500);

                // Create service set to Automatic startup
                RunSc($"create {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"");
            }
            
            // Set service description
            RunSc($"description {ServiceName} \"High-Performance Real-Time Network Traffic Control and Bandwidth Shaper Service\"");

            // Set failure recovery: restart after 5s on 1st crash, 10s on 2nd crash
            RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000//1000");

            // Start service immediately
            RunSc($"start {ServiceName}");
            Thread.Sleep(500);
        }
        catch { }
    }

    private void ConfigureAutoStartup(string targetExe, string targetDir)
    {
        try
        {
            string args = "--startup";
            string startupCmd = $"\"{targetExe}\" {args}";
            string iconPath = Path.Combine(targetDir, "Assets", "app.ico");
            if (!File.Exists(iconPath)) iconPath = targetExe;

            // 1. Registry HKLM Run (system-wide startup for all users)
            try
            {
                using var hklmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (hklmKey != null)
                {
                    hklmKey.SetValue("InternetLimiter", startupCmd);
                    hklmKey.DeleteValue("Valve", false);
                }
            }
            catch { }

            // 2. Registry HKCU Run (current user)
            try
            {
                using var hkcuKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (hkcuKey != null)
                {
                    hkcuKey.SetValue("InternetLimiter", startupCmd);
                    hkcuKey.DeleteValue("Valve", false);
                }
            }
            catch { }

            // 3. Common Startup folder shortcut (runs on any user login)
            try
            {
                string commonStartupDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                if (Directory.Exists(commonStartupDir))
                {
                    string startupShortcut = Path.Combine(commonStartupDir, "Internet Limiter.lnk");
                    CreateLnk(startupShortcut, targetExe, targetDir, iconPath, "Internet Limiter - Network Bandwidth Shaper & Firewall", args);
                }
            }
            catch { }
        }
        catch { }
    }

    private void CreateShortcuts(string targetExe, string targetDir)
    {
        try
        {
            string iconPath = Path.Combine(targetDir, "Assets", "app.ico");
            if (!File.Exists(iconPath)) iconPath = targetExe;

            // Desktop shortcut
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string desktopShortcut = Path.Combine(desktopDir, "Internet Limiter.lnk");
            CreateLnk(desktopShortcut, targetExe, targetDir, iconPath, "Internet Limiter - Network Bandwidth Shaper & Firewall");

            // Start Menu shortcut
            string startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            string startMenuShortcut = Path.Combine(startMenuDir, "Internet Limiter.lnk");
            CreateLnk(startMenuShortcut, targetExe, targetDir, iconPath, "Internet Limiter - Network Bandwidth Shaper & Firewall");
        }
        catch { }
    }

    private static void CreateLnk(string lnkPath, string targetExe, string workingDir, string iconLocation, string description, string arguments = "")
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = targetExe;
            shortcut.WorkingDirectory = workingDir;
            if (!string.IsNullOrEmpty(arguments)) shortcut.Arguments = arguments;
            shortcut.IconLocation = $"{iconLocation},0";
            shortcut.Description = description;
            shortcut.Save();
        }
        catch { }
    }

    private void RegisterUninstall(string installDir, string exePath)
    {
        try
        {
            string iconPath = Path.Combine(installDir, "Assets", "app.ico");
            if (!File.Exists(iconPath)) iconPath = exePath;

            using var key = Registry.LocalMachine.CreateSubKey(UninstallRegistryKey);
            if (key == null) return;

            key.SetValue("DisplayName", "Internet Limiter");
            key.SetValue("DisplayVersion", "1.0.0");
            key.SetValue("Publisher", "Internet Limiter Team");
            key.SetValue("InstallLocation", installDir);
            key.SetValue("DisplayIcon", $"{iconPath},0");
            key.SetValue("UninstallString", $"\"{exePath}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{exePath}\" --uninstall --quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    public static (int ExitCode, string Output) RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "Failed to start sc.exe");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (p.ExitCode, (stdout + "\n" + stderr).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
