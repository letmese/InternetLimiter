using System.Runtime.InteropServices;
using Valve.Interop;

namespace Valve.Core;

public sealed record ServiceInfo(string ServiceName, string DisplayName);

/// <summary>
/// Attributes Windows system service host processes (e.g. svchost.exe) to real Windows
/// service names and display names (DNS Client, Windows Update, BITS, DHCP, etc.)
/// using Advapi32 EnumServicesStatusEx.
/// </summary>
public static class ServiceResolver
{
    private static readonly Dictionary<int, ServiceInfo> _pidMap = new();
    private static DateTime _lastRefresh = DateTime.MinValue;
    private static readonly object _sync = new();

    public static ServiceInfo? GetServiceForPid(int pid)
    {
        if (pid <= 0) return null;

        lock (_sync)
        {
            if (DateTime.UtcNow - _lastRefresh > TimeSpan.FromSeconds(15) || _pidMap.Count == 0)
            {
                RefreshInternal();
            }

            return _pidMap.TryGetValue(pid, out var info) ? info : null;
        }
    }

    public static void Refresh()
    {
        lock (_sync)
        {
            RefreshInternal();
        }
    }

    private static void RefreshInternal()
    {
        IntPtr scm = Native.OpenSCManagerW(null, null, Native.SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero)
        {
            return;
        }

        try
        {
            uint bytesNeeded = 0;
            uint servicesReturned = 0;
            uint resumeHandle = 0;

            Native.EnumServicesStatusExW(
                scm,
                Native.SC_ENUM_PROCESS_INFO,
                Native.SERVICE_WIN32,
                Native.SERVICE_STATE_ALL,
                IntPtr.Zero,
                0,
                out bytesNeeded,
                out servicesReturned,
                ref resumeHandle,
                null);

            if (bytesNeeded == 0)
            {
                return;
            }

            IntPtr buf = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                resumeHandle = 0;
                if (Native.EnumServicesStatusExW(
                    scm,
                    Native.SC_ENUM_PROCESS_INFO,
                    Native.SERVICE_WIN32,
                    Native.SERVICE_STATE_ALL,
                    buf,
                    bytesNeeded,
                    out bytesNeeded,
                    out servicesReturned,
                    ref resumeHandle,
                    null))
                {
                    _pidMap.Clear();
                    int structSize = Marshal.SizeOf<Native.ENUM_SERVICE_STATUS_PROCESS>();
                    for (int i = 0; i < servicesReturned; i++)
                    {
                        IntPtr ptr = IntPtr.Add(buf, i * structSize);
                        var item = Marshal.PtrToStructure<Native.ENUM_SERVICE_STATUS_PROCESS>(ptr);
                        if (item.ServiceStatusProcess.dwProcessId > 0)
                        {
                            int pid = (int)item.ServiceStatusProcess.dwProcessId;
                            if (!_pidMap.ContainsKey(pid))
                            {
                                _pidMap[pid] = new ServiceInfo(item.lpServiceName ?? "", item.lpDisplayName ?? "");
                            }
                        }
                    }
                    _lastRefresh = DateTime.UtcNow;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            // Graceful fallback if SCM access is restricted
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }
    }
}
