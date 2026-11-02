using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Valve.Core;

/// <summary>
/// Manages Windows Service lifecycle, shortcut creation, and Add/Remove Programs registration.
/// </summary>
public static class ServiceManager
{
    public const string ServiceName = WindowsServiceDispatcher.ServiceName;
    public const string DisplayName = WindowsServiceDispatcher.DisplayName;
    public const string UninstallRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\InternetLimiter";

    public static (bool Success, string Message) InstallService(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "InternetLimiter.exe");
        if (!File.Exists(exePath))
        {
            string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Valve.exe");
            if (File.Exists(fallback)) exePath = fallback;
        }

        string binPath = $"\"{exePath}\" --service";

        try
        {
            // 1. If service already exists, stop and delete it first
            if (IsServiceInstalled())
            {
                RunSc($"stop {ServiceName}");
                RunSc($"delete {ServiceName}");
                Thread.Sleep(500);
            }

            // 2. Create the service
            var (code, output) = RunSc($"create {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"");
            if (code != 0 && !output.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Failed to create service: {output}");
            }

            // 3. Set description
            RunSc($"description {ServiceName} \"High-Performance Real-Time Network Traffic Control and Bandwidth Shaper Service\"");

            // 4. Configure auto-restart on failure (restart after 5s on 1st crash, 10s on 2nd crash)
            RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000//1000");

            // 5. Start the service
            var (startCode, startOutput) = RunSc($"start {ServiceName}");
            Thread.Sleep(500);

            return (true, "Service installed and started successfully.");
        }
        catch (Exception ex)
        {
            return (false, $"Exception during service installation: {ex.Message}");
        }
    }

    public static (bool Success, string Message) UninstallService()
    {
        try
        {
            RunSc($"stop {ServiceName}");
            var (code, output) = RunSc($"delete {ServiceName}");
            return (code == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Success, string Message) StartService()
    {
        var (code, output) = RunSc($"start {ServiceName}");
        return (code == 0, output);
    }

    public static (bool Success, string Message) StopService()
    {
        var (code, output) = RunSc($"stop {ServiceName}");
        return (code == 0, output);
    }

    public static bool IsServiceInstalled()
    {
        var (code, _) = RunSc($"query {ServiceName}");
        return code == 0;
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

    public static void CreateShortcuts(string targetExe, string? iconPath = null)
    {
        try
        {
            iconPath ??= Path.Combine(Path.GetDirectoryName(targetExe)!, "Assets", "app.ico");
            if (!File.Exists(iconPath)) iconPath = targetExe;

            string workingDir = Path.GetDirectoryName(targetExe)!;

            // Desktop shortcut
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string desktopShortcut = Path.Combine(desktopDir, "Internet Limiter.lnk");
            CreateLnk(desktopShortcut, targetExe, workingDir, iconPath, "Internet Limiter - Network Bandwidth Shaper & Firewall");

            // Start Menu shortcut
            string startMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            string startMenuShortcut = Path.Combine(startMenuPrograms, "Internet Limiter.lnk");
            CreateLnk(startMenuShortcut, targetExe, workingDir, iconPath, "Internet Limiter - Network Bandwidth Shaper & Firewall");
        }
        catch (Exception ex)
        {
            EventLogBridge.Warn("Failed to create shortcuts: " + ex.Message);
        }
    }

    public static void RemoveShortcuts()
    {
        try
        {
            string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Internet Limiter.lnk");
            if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);

            string startMenuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Internet Limiter.lnk");
            if (File.Exists(startMenuShortcut)) File.Delete(startMenuShortcut);
        }
        catch { }
    }

    private static void CreateLnk(string lnkPath, string targetExe, string workingDir, string iconLocation, string description)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = targetExe;
            shortcut.WorkingDirectory = workingDir;
            shortcut.IconLocation = $"{iconLocation},0";
            shortcut.Description = description;
            shortcut.Save();
        }
        catch (Exception ex)
        {
            EventLogBridge.Warn($"Failed to create shortcut at {lnkPath}: {ex.Message}");
        }
    }

    public static void RegisterUninstall(string installDir, string exePath, string iconPath)
    {
        try
        {
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
        catch (Exception ex)
        {
            EventLogBridge.Warn("Failed to register in Add/Remove Programs: " + ex.Message);
        }
    }

    public static void UnregisterUninstall()
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(UninstallRegistryKey, false);
        }
        catch { }
    }
}
