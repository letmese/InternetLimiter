using System.Runtime.InteropServices;

namespace Valve.Core;

/// <summary>
/// Native Windows Service Control Manager (SCM) dispatcher.
/// Allows InternetLimiter.exe --service to run either as a true registered Windows Service
/// (via StartServiceCtrlDispatcher) or interactively in a console/terminal session.
/// </summary>
public static class WindowsServiceDispatcher
{
    public const string ServiceName = "InternetLimiterService";
    public const string DisplayName = "Internet Limiter Service";

    private const int SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const int SERVICE_STOPPED = 0x00000001;
    private const int SERVICE_START_PENDING = 0x00000002;
    private const int SERVICE_STOP_PENDING = 0x00000003;
    private const int SERVICE_RUNNING = 0x00000004;

    private const int SERVICE_CONTROL_STOP = 0x00000001;
    private const int SERVICE_CONTROL_SHUTDOWN = 0x00000005;

    private const int SERVICE_ACCEPT_STOP = 0x00000001;
    private const int SERVICE_ACCEPT_SHUTDOWN = 0x00000004;

    private const int NO_ERROR = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    private delegate void ServiceMainDelegate(int argc, IntPtr argv);
    private delegate void ServiceCtrlHandlerDelegate(int opcode);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_TABLE_ENTRY
    {
        public IntPtr lpServiceName;
        public IntPtr lpServiceProc;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcher(IntPtr lpServiceTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandler(string lpServiceName, ServiceCtrlHandlerDelegate lpHandlerProc);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr hServiceStatus, ref SERVICE_STATUS lpServiceStatus);

    private static IntPtr _statusHandle;
    private static ValveServiceHost? _host;
    private static ServiceMainDelegate? _serviceMainDelegate;
    private static ServiceCtrlHandlerDelegate? _serviceCtrlDelegate;

    public static void RunAsServiceOrInteractive()
    {
        if (OperatingSystem.IsWindows())
        {
            _serviceMainDelegate = ServiceMain;
            IntPtr serviceNamePtr = Marshal.StringToHGlobalUni(ServiceName);
            IntPtr serviceProcPtr = Marshal.GetFunctionPointerForDelegate(_serviceMainDelegate);

            IntPtr tablePtr = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_TABLE_ENTRY>() * 2);
            try
            {
                var entries = new SERVICE_TABLE_ENTRY[2];
                entries[0].lpServiceName = serviceNamePtr;
                entries[0].lpServiceProc = serviceProcPtr;
                entries[1].lpServiceName = IntPtr.Zero;
                entries[1].lpServiceProc = IntPtr.Zero;

                Marshal.StructureToPtr(entries[0], tablePtr, false);
                Marshal.StructureToPtr(entries[1], tablePtr + Marshal.SizeOf<SERVICE_TABLE_ENTRY>(), false);

                bool ok = StartServiceCtrlDispatcher(tablePtr);
                if (!ok)
                {
                    // Failed to connect to SCM, which means it was launched from an interactive shell.
                    RunInteractive();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(serviceNamePtr);
                Marshal.FreeHGlobal(tablePtr);
            }
        }
        else
        {
            RunInteractive();
        }
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _serviceCtrlDelegate = ServiceCtrlHandler;
        _statusHandle = RegisterServiceCtrlHandler(ServiceName, _serviceCtrlDelegate);
        if (_statusHandle == IntPtr.Zero) return;

        UpdateStatus(SERVICE_START_PENDING);

        try
        {
            _host = new ValveServiceHost();
            UpdateStatus(SERVICE_RUNNING, SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN);
            _host.Run();
        }
        catch (Exception ex)
        {
            EventLogBridge.Warn("Internet Limiter ServiceMain error: " + ex.Message);
        }
        finally
        {
            UpdateStatus(SERVICE_STOPPED);
            _host?.Dispose();
            _host = null;
        }
    }

    private static void ServiceCtrlHandler(int opcode)
    {
        if (opcode is SERVICE_CONTROL_STOP or SERVICE_CONTROL_SHUTDOWN)
        {
            UpdateStatus(SERVICE_STOP_PENDING);
            _host?.Stop();
        }
    }

    private static void UpdateStatus(int state, int accepted = 0)
    {
        if (_statusHandle == IntPtr.Zero) return;
        var status = new SERVICE_STATUS
        {
            dwServiceType = SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = accepted,
            dwWin32ExitCode = NO_ERROR
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    private static void RunInteractive()
    {
        Console.WriteLine($"Starting {DisplayName} interactively... (Press Ctrl+C to stop)");
        using var host = new ValveServiceHost();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Stopping service host...");
            host.Stop();
        };
        host.Run();
    }
}
