using System.Runtime.InteropServices;

namespace Valve.Interop;

/// <summary>
/// P/Invoke surface for the documented Windows networking APIs Valve is built on.
/// Nothing here is undocumented behaviour: GetExtendedTcpTable gives connection
/// ownership, GetPerTcp*ConnectionEStats gives byte counters, both are public.
/// </summary>
internal static class Native
{
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;

    public const int TCP_TABLE_OWNER_PID_ALL = 5;
    public const int UDP_TABLE_OWNER_PID = 1;
    public const int TCP_ESTATS_DATA = 1;
    public const int TCP_ESTATS_PATH = 3;
    public const int MIB_TCP_STATE_LISTEN = 2;
    public const int MIB_TCP_STATE_ESTAB = 5;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MIB_TCP6ROW_OWNER_PID
    {
        public fixed byte ucLocalAddr[16];
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public fixed byte ucRemoteAddr[16];
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MIB_UDP6ROW_OWNER_PID
    {
        public fixed byte ucLocalAddr[16];
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TCP_ESTATS_DATA_ROD_v0
    {
        public ulong DataBytesOut;
        public ulong DataSegsOut;
        public ulong DataBytesIn;
        public ulong DataSegsIn;
        public ulong SegsOut;
        public ulong SegsIn;
        public uint SoftErrors;
        public uint SoftErrorReason;
        public uint SndUna;
        public uint SndNxt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TCP_ESTATS_DATA_RW_v0
    {
        public byte EnableCollection;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TCP_ESTATS_PATH_ROD_v0
    {
        public uint FastRetran;
        public uint Timeouts;
        public uint SubsequentTimeouts;
        public uint CurTimeoutInt;
        public uint MaxTimeoutInt;
        public uint MinTimeoutInt;
        public uint LimRto;
        public uint CurMss;
        public uint MaxMss;
        public uint MinMss;
        public uint AutoCurMss;
        public uint AutoMaxMss;
        public uint AutoMinMss;
        public uint CurRtt;          // Current RTT in microseconds
        public uint SmoothedRtt;    // Smoothed RTT in microseconds
        public uint RttVar;         // Variance
        public uint MinRtt;
        public uint MaxRtt;
        public uint CurReasmQueue;
        public uint MaxReasmQueue;
        public ulong BytesRetrans;   // Retransmission volume in bytes
        public ulong PktsRetrans;
        public ulong SoftErrors;
        public ulong SoftErrorReason;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TCP_ESTATS_PATH_RW_v0
    {
        public byte EnableCollection;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint SetPerTcpConnectionEStats(
        IntPtr row, int estatsType,
        IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize,
        IntPtr rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetPerTcpConnectionEStats(
        IntPtr row, int estatsType,
        IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize,
        IntPtr rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint SetPerTcp6ConnectionEStats(
        IntPtr row, int estatsType,
        IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize,
        IntPtr rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetPerTcp6ConnectionEStats(
        IntPtr row, int estatsType,
        IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize,
        IntPtr rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint SetTcpEntry(IntPtr pTcprow);

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MIB_TCP6ROW
    {
        public uint dwState;
        public fixed byte ucLocalAddr[16];
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public fixed byte ucRemoteAddr[16];
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(
        IntPtr process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    public const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("shell32.dll")]
    public static extern bool IsUserAnAdmin();

    /// <summary>Snapshot of the owner-PID TCP table for one address family.</summary>
    public static byte[]? ReadTcpTable(int af, out uint error)
    {
        int size = 0;
        error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        if (error != 0 && error != 122) // ERROR_INSUFFICIENT_BUFFER is expected
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            error = GetExtendedTcpTable(buffer, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
            if (error != 0)
            {
                return null;
            }

            byte[] managed = new byte[size];
            Marshal.Copy(buffer, managed, 0, size);
            return managed;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Snapshot of the owner-PID UDP table for one address family.</summary>
    public static byte[]? ReadUdpTable(int af, out uint error)
    {
        int size = 0;
        error = GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
        if (error != 0 && error != 122) // ERROR_INSUFFICIENT_BUFFER is expected
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            error = GetExtendedUdpTable(buffer, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
            if (error != 0)
            {
                return null;
            }

            byte[] managed = new byte[size];
            Marshal.Copy(buffer, managed, 0, size);
            return managed;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string ProcessImagePath(uint pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            char[] buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? new string(buffer, 0, (int)size)
                : string.Empty;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static Dictionary<ushort, int> GetAllPortOwners()
    {
        var map = new Dictionary<ushort, int>();

        // TCP IPv4
        ReadPortRows(AF_INET, true, 24, ptr =>
        {
            unsafe
            {
                var row = (MIB_TCPROW_OWNER_PID*)ptr;
                ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(row->dwLocalPort & 0xFFFF));
                if (port > 0 && row->dwOwningPid > 0) map[port] = (int)row->dwOwningPid;
            }
        });

        // TCP IPv6
        ReadPortRows(AF_INET6, true, 56, ptr =>
        {
            unsafe
            {
                var row = (MIB_TCP6ROW_OWNER_PID*)ptr;
                ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(row->dwLocalPort & 0xFFFF));
                if (port > 0 && row->dwOwningPid > 0) map[port] = (int)row->dwOwningPid;
            }
        });

        // UDP IPv4
        ReadPortRows(AF_INET, false, 12, ptr =>
        {
            unsafe
            {
                var row = (MIB_UDPROW_OWNER_PID*)ptr;
                ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(row->dwLocalPort & 0xFFFF));
                if (port > 0 && row->dwOwningPid > 0) map[port] = (int)row->dwOwningPid;
            }
        });

        // UDP IPv6
        ReadPortRows(AF_INET6, false, 28, ptr =>
        {
            unsafe
            {
                var row = (MIB_UDP6ROW_OWNER_PID*)ptr;
                ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(row->dwLocalPort & 0xFFFF));
                if (port > 0 && row->dwOwningPid > 0) map[port] = (int)row->dwOwningPid;
            }
        });

        return map;
    }

    private static void ReadPortRows(int af, bool tcp, int stride, Action<IntPtr> onRow)
    {
        int size = 0;
        uint res = tcp
            ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
            : GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, UDP_TABLE_OWNER_PID, 0);

        if (size <= 0) return;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            res = tcp
                ? GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
                : GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);

            if (res != 0) return;

            int count = Marshal.ReadInt32(buf);
            IntPtr ptr = IntPtr.Add(buf, 4);
            for (int i = 0; i < count; i++)
            {
                onRow(ptr);
                ptr = IntPtr.Add(ptr, stride);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    public static void SetWindowFrameTheme(IntPtr hwnd, bool isDark)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int darkVal = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkVal, sizeof(int));
            int corners = 2; // DWMWCP_ROUND (Windows 11 rounded window corners)
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        }
        catch { }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool EnumServicesStatusExW(
        IntPtr hSCManager,
        int infoLevel,
        uint serviceType,
        uint serviceState,
        IntPtr lpServices,
        uint cbBufSize,
        out uint pcbBytesNeeded,
        out uint lpServicesReturned,
        ref uint lpResumeHandle,
        string? pszGroupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    public const int SC_ENUM_PROCESS_INFO = 0;
    public const uint SERVICE_WIN32 = 0x00000030;
    public const uint SERVICE_STATE_ALL = 0x00000003;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ENUM_SERVICE_STATUS_PROCESS
    {
        public string lpServiceName;
        public string lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
    public static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
    public static extern uint TimeEndPeriod(uint uPeriod);

    public const uint MIB_TCP_STATE_DELETE_TCB = 12;

    public static void PopulatePortOwnerArrays(int[] tcpOwners, int[] udpOwners)
    {
        Array.Clear(tcpOwners, 0, tcpOwners.Length);
        Array.Clear(udpOwners, 0, udpOwners.Length);

        // TCP IPv4
        ReadPortRows(AF_INET, true, 24, ptr =>
        {
            unsafe
            {
                var row = (MIB_TCPROW_OWNER_PID*)ptr;
                ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(row->dwLocalPort & 0xFFFF));
                if (port > 0 && row->dwOwningPid > 0) tcpOwners[port] = (int)row->dwOwningPid;
            }
        });

        // TCP IPv6
        ReadPortRows(AF_INET6, true, 56, ptr =>
        {
            unsafe
            {
                var row = (MIB_TCP6ROW_OWNER_PID*)ptr;
                ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(row->dwLocalPort & 0xFFFF));
                if (port > 0 && row->dwOwningPid > 0) tcpOwners[port] = (int)row->dwOwningPid;
            }
        });

        // UDP IPv4
        ReadPortRows(AF_INET, false, 12, ptr =>
        {
            unsafe
            {
                var row = (MIB_UDPROW_OWNER_PID*)ptr;
                ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(row->dwLocalPort & 0xFFFF));
                if (port > 0 && row->dwOwningPid > 0) udpOwners[port] = (int)row->dwOwningPid;
            }
        });

        // UDP IPv6
        ReadPortRows(AF_INET6, false, 28, ptr =>
        {
            unsafe
            {
                var row = (MIB_UDP6ROW_OWNER_PID*)ptr;
                ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(row->dwLocalPort & 0xFFFF));
                if (port > 0 && row->dwOwningPid > 0) udpOwners[port] = (int)row->dwOwningPid;
            }
        });
    }

    // --- Win32 HotKey API (Item 4.09) ---
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

