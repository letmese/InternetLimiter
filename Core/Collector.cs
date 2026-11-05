using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Valve.Interop;

namespace Valve.Core;

/// <summary>
/// Static set of IPv4/IPv6 addresses owned by this host, used to tell
/// "my own socket" connections from forwarded/transit traffic. Refreshed
/// lazily (network changes are rare; staleness just re-flags a row).
/// </summary>
public static class LocalAddresses
{
    private static DateTime _refreshedAt;
    private static readonly HashSet<string> _v4 = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _v6 = new(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string ip)
    {
        RefreshIfStale();
        string normalized = ip.Trim().ToLowerInvariant();
        if (normalized.Length == 0 || normalized == "*" || normalized == "0.0.0.0" || normalized == "::" || normalized == "::0")
        {
            return false;
        }

        return _v4.Contains(normalized) || _v6.Contains(normalized);
    }

    /// <summary>Loops through adapter unicast addresses, tolerating an elevated
    /// app that sees the world through an admin network namespace.</summary>
    private static void RefreshIfStale()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _refreshedAt < TimeSpan.FromMinutes(5))
        {
            return;
        }

        lock (_v4)
        {
            if (now - _refreshedAt < TimeSpan.FromMinutes(5))
            {
                return;
            }

            _refreshedAt = now;
            _v4.Clear();
            _v6.Clear();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            _v4.Add(addr.Address.ToString().ToLowerInvariant());
                        }
                        else if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        {
                            _v6.Add(addr.Address.ToString().ToLowerInvariant());
                        }
                    }
                }
            }
            catch
            {
                // Network inspection can throw in sandboxed contexts; keep the cached set.
            }
        }
    }
}

/// <summary>
/// Samples every established TCP connection once per interval, reads its byte
/// counters, and reports per-connection and per-process rates.
///
/// Deliberately does not touch the UI thread: <see cref="Sampled"/> fires on a
/// worker thread and the consumer is responsible for dispatching.
/// </summary>
public sealed class Collector : IDisposable
{
    private const double Alpha = 0.45;
    private const double IdleFloor = 240; // bytes/s below which a connection is considered quiet

    private readonly object _gate = new();
    private readonly Dictionary<string, ConnState> _conns = new();
    private readonly Dictionary<int, string> _paths = new();
    private readonly HashSet<string> _enabled = new();
    private readonly Timer _timer;
    private double _intervalSeconds;

    private DateTime _lastAt;
    private bool _denied;
    private string? _error;
    private bool _disposed;

    public Collector(double intervalSeconds = 1.0)
    {
        _intervalSeconds = intervalSeconds;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool Elevated { get; } = Native.IsUserAnAdmin();

    public bool IsPaused { get; private set; }

    public double IntervalSeconds
    {
        get => _intervalSeconds;
        set
        {
            _intervalSeconds = Math.Clamp(value, 0.25, 10);
            if (!_disposed && !IsPaused)
            {
                _timer.Change(0, (int)(_intervalSeconds * 1000));
            }
        }
    }

    public void Pause()
    {
        IsPaused = true;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public void Resume()
    {
        IsPaused = false;
        if (!_disposed)
        {
            _timer.Change(0, (int)(_intervalSeconds * 1000));
        }
    }

    public event Action<TrafficSample>? Sampled;

    public void Start()
    {
        _lastAt = DateTime.UtcNow;
        _timer.Change(0, (int)(_intervalSeconds * 1000));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    private void Tick()
    {
        try
        {
            TrafficSample sample = Sample();
            Sampled?.Invoke(sample);
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Last non-zero return code from GetPerTcpConnectionEStats.</summary>
    public static uint LastEstatsError { get; private set; }

    private TrafficSample Sample()
    {
        DateTime now = DateTime.UtcNow;
        double dt = Math.Max(0.2, (now - _lastAt).TotalSeconds);
        _lastAt = now;

        var readings = new List<ConnReading>(64);
        var live = new HashSet<string>();
        var listeningPorts = new HashSet<int>();

        // Pre-pass: identify active listening ports for inbound connection detection
        foreach (bool ipv6 in new[] { false, true })
        {
            byte[]? tcpTable = Native.ReadTcpTable(ipv6 ? Native.AF_INET6 : Native.AF_INET, out _);
            if (tcpTable is not null)
            {
                CollectListeningPorts(tcpTable, ipv6, listeningPorts);
            }
        }

        foreach (bool ipv6 in new[] { false, true })
        {
            byte[]? table = Native.ReadTcpTable(ipv6 ? Native.AF_INET6 : Native.AF_INET, out _);
            if (table is not null)
            {
                try
                {
                    ReadFamily(table, ipv6, dt, readings, live, listeningPorts);
                }
                catch (Exception ex)
                {
                    // Never let one malformed family kill the whole sample silently.
                    _error = $"sample failed ({(ipv6 ? "v6" : "v4")}): {ex.GetType().Name}: {ex.Message}";
                }
            }

            // UDP endpoints query
            byte[]? udpTable = Native.ReadUdpTable(ipv6 ? Native.AF_INET6 : Native.AF_INET, out _);
            if (udpTable is not null)
            {
                try
                {
                    ReadUdpFamily(udpTable, ipv6, readings, live);
                }
                catch (Exception ex)
                {
                    _error = $"udp sample failed ({(ipv6 ? "v6" : "v4")}): {ex.GetType().Name}: {ex.Message}";
                }
            }
        }

        lock (_gate)
        {
            foreach (string key in _conns.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _conns.Remove(key);
            }

            _enabled.RemoveWhere(k => !live.Contains(k));
        }

        string? note = null;
        if (!Elevated)
        {
            note = "Traffic counters need administrator rights.";
        }
        else if (_denied)
        {
            note = "Connections listed, but byte counters need administrator rights.";
        }
        else if (_error is not null)
        {
            note = _error;
        }

        return new TrafficSample(now, readings, Elevated, note);
    }

    private unsafe void CollectListeningPorts(byte[] table, bool ipv6, HashSet<int> listeningPorts)
    {
        fixed (byte* basePtr = table)
        {
            int count = *(int*)basePtr;
            int stride = ipv6 ? 56 : 24;

            for (int i = 0; i < count; i++)
            {
                byte* rowPtr = basePtr + 4 + (i * stride);
                uint state = ipv6 ? ((Native.MIB_TCP6ROW_OWNER_PID*)rowPtr)->dwState
                                  : ((Native.MIB_TCPROW_OWNER_PID*)rowPtr)->dwState;
                if (state == Native.MIB_TCP_STATE_LISTEN)
                {
                    int localPort = ipv6 ? Port(((Native.MIB_TCP6ROW_OWNER_PID*)rowPtr)->dwLocalPort)
                                         : Port(((Native.MIB_TCPROW_OWNER_PID*)rowPtr)->dwLocalPort);
                    if (localPort > 0)
                    {
                        listeningPorts.Add(localPort);
                    }
                }
            }
        }
    }

    private unsafe void ReadUdpFamily(byte[] table, bool ipv6, List<ConnReading> readings, HashSet<string> live)
    {
        fixed (byte* basePtr = table)
        {
            int count = *(int*)basePtr;
            int stride = ipv6 ? 28 : 12;

            for (int i = 0; i < count; i++)
            {
                byte* rowPtr = basePtr + 4 + (i * stride);
                uint pid = ipv6 ? ((Native.MIB_UDP6ROW_OWNER_PID*)rowPtr)->dwOwningPid
                                : ((Native.MIB_UDPROW_OWNER_PID*)rowPtr)->dwOwningPid;
                if (pid == 0) continue;

                string local;
                int localPort;
                if (ipv6)
                {
                    var row = (Native.MIB_UDP6ROW_OWNER_PID*)rowPtr;
                    local = V6(row->ucLocalAddr, 0);
                    localPort = Port(row->dwLocalPort);
                }
                else
                {
                    var row = (Native.MIB_UDPROW_OWNER_PID*)rowPtr;
                    local = V4(row->dwLocalAddr);
                    localPort = Port(row->dwLocalPort);
                }

                string key = $"udp|{(ipv6 ? "6" : "4")}|{local}:{localPort}|{pid}";
                live.Add(key);

                readings.Add(new ConnReading(
                    key, (int)pid, ipv6 ? "UDP6" : "UDP4",
                    $"{local}:{localPort}", "*:*", "*",
                    0, 0, 0, 0, (ushort)localPort,
                    IsInbound: true,
                    IsForwarded: !LocalAddresses.Contains(local)));
            }
        }
    }

    private unsafe void ReadFamily(byte[] table, bool ipv6, double dt, List<ConnReading> readings, HashSet<string> live, HashSet<int> listeningPorts)
    {
        fixed (byte* basePtr = table)
        {
            int count = *(int*)basePtr;
            int stride = ipv6 ? 56 : 24;

            for (int i = 0; i < count; i++)
            {
                byte* rowPtr = basePtr + 4 + (i * stride);

                uint state = ipv6 ? ((Native.MIB_TCP6ROW_OWNER_PID*)rowPtr)->dwState
                                  : ((Native.MIB_TCPROW_OWNER_PID*)rowPtr)->dwState;
                if (state != Native.MIB_TCP_STATE_ESTAB)
                {
                    continue;
                }

                uint pid = ipv6 ? ((Native.MIB_TCP6ROW_OWNER_PID*)rowPtr)->dwOwningPid
                                : ((Native.MIB_TCPROW_OWNER_PID*)rowPtr)->dwOwningPid;

                string local;
                string remote;
                int localPort;
                int remotePort;
                string key;

                if (ipv6)
                {
                    var row = (Native.MIB_TCP6ROW_OWNER_PID*)rowPtr;
                    local = V6(row->ucLocalAddr, 0);
                    remote = V6(row->ucRemoteAddr, 0);
                    localPort = Port(row->dwLocalPort);
                    remotePort = Port(row->dwRemotePort);
                    key = $"6|{local}:{localPort}|{remote}:{remotePort}";
                }
                else
                {
                    var row = (Native.MIB_TCPROW_OWNER_PID*)rowPtr;
                    local = V4(row->dwLocalAddr);
                    remote = V4(row->dwRemoteAddr);
                    localPort = Port(row->dwLocalPort);
                    remotePort = Port(row->dwRemotePort);
                    key = $"4|{local}:{localPort}|{remote}:{remotePort}";
                }

                live.Add(key);
                bool isInbound = listeningPorts.Contains(localPort);

                if (!_enabled.Contains(key))
                {
                    uint rc = ipv6 ? EnableV6(rowPtr) : EnableV4(rowPtr);
                    EnablePath(rowPtr, ipv6);
                    if (rc != 0)
                    {
                        if (rc == 5)
                        {
                            _denied = true;
                        }

                        // Without counters we can still report that the connection
                        // exists, which is more useful than an empty table.
                        readings.Add(new ConnReading(
                            key, (int)pid, ipv6 ? "TCP6" : "TCP4",
                            $"{local}:{localPort}", $"{remote}:{remotePort}", remote,
                            0, 0, 0, 0, (ushort)localPort,
                            IsInbound: isInbound,
                            IsForwarded: !LocalAddresses.Contains(local)));
                        continue;
                    }

                    _enabled.Add(key);
                }

                uint rttMs = 0;
                ulong retransBytes = 0;
                TryReadPath(rowPtr, ipv6, out rttMs, out retransBytes);

                if (!TryRead(rowPtr, ipv6, out ulong bytesIn, out ulong bytesOut))
                {
                    // Counters unavailable for this connection. Report it with zero
                    // rates rather than dropping the row - a connection that exists
                    // but cannot be measured is still a connection the user needs
                    // to see. Dropping it here is what emptied the whole table.
                    readings.Add(new ConnReading(
                        key, (int)pid, ipv6 ? "TCP6" : "TCP4",
                        $"{local}:{localPort}", $"{remote}:{remotePort}", remote,
                        0, 0, 0, 0, (ushort)localPort,
                        IsInbound: isInbound,
                        RttMs: rttMs,
                        RetransBytes: retransBytes,
                        IsForwarded: !LocalAddresses.Contains(local)));
                    continue;
                }

                ConnState state2;
                if (!_conns.TryGetValue(key, out state2!))
                {
                    state2 = new ConnState((int)pid);
                    _conns[key] = state2;
                }

                long deltaIn = (long)bytesIn - state2.PrevIn;
                long deltaOut = (long)bytesOut - state2.PrevOut;
                if (deltaIn < 0)
                {
                    deltaIn = 0; // counter reset (connection reused)
                }

                if (deltaOut < 0)
                {
                    deltaOut = 0;
                }

                state2.PrevIn = (long)bytesIn;
                state2.PrevOut = (long)bytesOut;
                state2.TotalIn += deltaIn;
                state2.TotalOut += deltaOut;
                state2.Seen = DateTime.UtcNow;

                double instantIn = deltaIn / dt;
                double instantOut = deltaOut / dt;
                state2.InRate = (Alpha * instantIn) + ((1 - Alpha) * state2.InRate);
                state2.OutRate = (Alpha * instantOut) + ((1 - Alpha) * state2.OutRate);

                readings.Add(new ConnReading(
                    key, (int)pid, ipv6 ? "TCP6" : "TCP4",
                    $"{local}:{localPort}", $"{remote}:{remotePort}", remote,
                    state2.InRate, state2.OutRate, state2.TotalIn, state2.TotalOut, (ushort)localPort,
                    IsInbound: isInbound,
                    RttMs: rttMs,
                    RetransBytes: retransBytes,
                    IsForwarded: !LocalAddresses.Contains(local)));
            }
        }
    }

    private static unsafe uint EnableV4(byte* row)
    {
        var rw = new Native.TCP_ESTATS_DATA_RW_v0 { EnableCollection = 1 };
        IntPtr rwPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.TCP_ESTATS_DATA_RW_v0>());
        try
        {
            Marshal.StructureToPtr(rw, rwPtr, false);
            return Native.SetPerTcpConnectionEStats(
                (IntPtr)row, Native.TCP_ESTATS_DATA,
                rwPtr, 0, (uint)Marshal.SizeOf<Native.TCP_ESTATS_DATA_RW_v0>(),
                IntPtr.Zero, 0, 0,
                IntPtr.Zero, 0, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(rwPtr);
        }
    }

    private static unsafe uint EnableV6(byte* row)
    {
        var rw = new Native.TCP_ESTATS_DATA_RW_v0 { EnableCollection = 1 };
        IntPtr rwPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.TCP_ESTATS_DATA_RW_v0>());
        try
        {
            Marshal.StructureToPtr(rw, rwPtr, false);
            return Native.SetPerTcp6ConnectionEStats(
                (IntPtr)row, Native.TCP_ESTATS_DATA,
                rwPtr, 0, (uint)Marshal.SizeOf<Native.TCP_ESTATS_DATA_RW_v0>(),
                IntPtr.Zero, 0, 0,
                IntPtr.Zero, 0, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(rwPtr);
        }
    }

    private static unsafe void EnablePath(byte* row, bool ipv6)
    {
        var rw = new Native.TCP_ESTATS_PATH_RW_v0 { EnableCollection = 1 };
        int size = Marshal.SizeOf<Native.TCP_ESTATS_PATH_RW_v0>();
        IntPtr rwPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(rw, rwPtr, false);
            if (ipv6)
            {
                Native.SetPerTcp6ConnectionEStats(
                    (IntPtr)row, Native.TCP_ESTATS_PATH,
                    rwPtr, 0, (uint)size,
                    IntPtr.Zero, 0, 0,
                    IntPtr.Zero, 0, 0);
            }
            else
            {
                Native.SetPerTcpConnectionEStats(
                    (IntPtr)row, Native.TCP_ESTATS_PATH,
                    rwPtr, 0, (uint)size,
                    IntPtr.Zero, 0, 0,
                    IntPtr.Zero, 0, 0);
            }
        }
        catch
        {
        }
        finally
        {
            Marshal.FreeHGlobal(rwPtr);
        }
    }

    private static unsafe bool TryReadPath(byte* row, bool ipv6, out uint rttMs, out ulong retransBytes)
    {
        Native.TCP_ESTATS_PATH_ROD_v0 rod = default;
        int size = sizeof(Native.TCP_ESTATS_PATH_ROD_v0);
        try
        {
            uint rc = ipv6
                ? Native.GetPerTcp6ConnectionEStats(
                    (IntPtr)row, Native.TCP_ESTATS_PATH,
                    IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0,
                    (IntPtr)(&rod), 0, (uint)size)
                : Native.GetPerTcpConnectionEStats(
                    (IntPtr)row, Native.TCP_ESTATS_PATH,
                    IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0,
                    (IntPtr)(&rod), 0, (uint)size);

            if (rc != 0)
            {
                rttMs = 0;
                retransBytes = 0;
                return false;
            }

            rttMs = rod.SmoothedRtt > 1000 ? rod.SmoothedRtt / 1000 : rod.SmoothedRtt;
            retransBytes = rod.BytesRetrans;
            return true;
        }
        catch
        {
            rttMs = 0;
            retransBytes = 0;
            return false;
        }
    }

    private static unsafe bool TryRead(byte* row, bool ipv6, out ulong bytesIn, out ulong bytesOut)
    {
        Native.TCP_ESTATS_DATA_ROD_v0 rod = default;
        int size = sizeof(Native.TCP_ESTATS_DATA_ROD_v0);
        uint rc = ipv6
            ? Native.GetPerTcp6ConnectionEStats(
                (IntPtr)row, Native.TCP_ESTATS_DATA,
                IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0,
                (IntPtr)(&rod), 0, (uint)size)
            : Native.GetPerTcpConnectionEStats(
                (IntPtr)row, Native.TCP_ESTATS_DATA,
                IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0,
                (IntPtr)(&rod), 0, (uint)size);

        if (rc != 0)
        {
            LastEstatsError = rc;
            bytesIn = 0;
            bytesOut = 0;
            return false;
        }

        bytesIn = rod.DataBytesIn;
        bytesOut = rod.DataBytesOut;
        return true;
    }

    /// <summary>
    /// Tears down every established IPv4 connection owned by a process, the way
    /// NetLimiter's "kill connections" does. Requires elevation; returns how many
    /// connections the stack accepted a delete for.
    /// </summary>
    public static unsafe int KillConnections(int pid)
    {
        byte[]? table = Native.ReadTcpTable(Native.AF_INET, out _);
        if (table is null)
        {
            return 0;
        }

        const uint DeleteTcb = 12;
        int killed = 0;

        fixed (byte* basePtr = table)
        {
            int count = *(int*)basePtr;
            for (int i = 0; i < count; i++)
            {
                var row = (Native.MIB_TCPROW_OWNER_PID*)(basePtr + 4 + (i * 24));
                if ((pid > 0 && row->dwOwningPid != (uint)pid) || row->dwState != Native.MIB_TCP_STATE_ESTAB)
                {
                    continue;
                }

                var entry = new Native.MIB_TCPROW
                {
                    dwState = DeleteTcb,
                    dwLocalAddr = row->dwLocalAddr,
                    dwLocalPort = row->dwLocalPort,
                    dwRemoteAddr = row->dwRemoteAddr,
                    dwRemotePort = row->dwRemotePort,
                };

                IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<Native.MIB_TCPROW>());
                try
                {
                    Marshal.StructureToPtr(entry, buffer, false);
                    if (Native.SetTcpEntry(buffer) == 0)
                    {
                        killed++;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        return killed;
    }

    public static unsafe bool KillConnection(string key)
    {
        byte[]? table = Native.ReadTcpTable(Native.AF_INET, out _);
        if (table is null)
        {
            return false;
        }

        const uint DeleteTcb = 12;
        fixed (byte* basePtr = table)
        {
            int count = *(int*)basePtr;
            for (int i = 0; i < count; i++)
            {
                var row = (Native.MIB_TCPROW_OWNER_PID*)(basePtr + 4 + (i * 24));
                string local = V4(row->dwLocalAddr);
                string remote = V4(row->dwRemoteAddr);
                int localPort = Port(row->dwLocalPort);
                int remotePort = Port(row->dwRemotePort);
                string k = $"4|{local}:{localPort}|{remote}:{remotePort}";

                if (k == key)
                {
                    var entry = new Native.MIB_TCPROW
                    {
                        dwState = DeleteTcb,
                        dwLocalAddr = row->dwLocalAddr,
                        dwLocalPort = row->dwLocalPort,
                        dwRemoteAddr = row->dwRemoteAddr,
                        dwRemotePort = row->dwRemotePort,
                    };

                    IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<Native.MIB_TCPROW>());
                    try
                    {
                        Marshal.StructureToPtr(entry, buffer, false);
                        return Native.SetTcpEntry(buffer) == 0;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
        }
        return false;
    }

    public static unsafe bool KillTcpConnection(string localIp, int localPort, string remoteIp, int remotePort)
    {
        byte[]? table = Native.ReadTcpTable(Native.AF_INET, out _);
        if (table is null)
        {
            return false;
        }

        const uint DeleteTcb = 12;
        fixed (byte* basePtr = table)
        {
            int count = *(int*)basePtr;
            for (int i = 0; i < count; i++)
            {
                var row = (Native.MIB_TCPROW_OWNER_PID*)(basePtr + 4 + (i * 24));
                string local = V4(row->dwLocalAddr);
                string remote = V4(row->dwRemoteAddr);
                int lPort = Port(row->dwLocalPort);
                int rPort = Port(row->dwRemotePort);

                if (local == localIp && lPort == localPort && remote == remoteIp && rPort == remotePort)
                {
                    var entry = new Native.MIB_TCPROW
                    {
                        dwState = DeleteTcb,
                        dwLocalAddr = row->dwLocalAddr,
                        dwLocalPort = row->dwLocalPort,
                        dwRemoteAddr = row->dwRemoteAddr,
                        dwRemotePort = row->dwRemotePort,
                    };

                    IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<Native.MIB_TCPROW>());
                    try
                    {
                        Marshal.StructureToPtr(entry, buffer, false);
                        return Native.SetTcpEntry(buffer) == 0;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
        }
        return false;
    }

    public string DisplayName(int pid)    {
        lock (_gate)
        {
            if (_paths.TryGetValue(pid, out string? cached))
            {
                return cached;
            }
        }

        string path = Native.ProcessImagePath((uint)pid);
        lock (_gate)
        {
            _paths[pid] = path;
        }

        return path;
    }

    private static string V4(uint address)
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)address;
        bytes[1] = (byte)(address >> 8);
        bytes[2] = (byte)(address >> 16);
        bytes[3] = (byte)(address >> 24);
        return new IPAddress(bytes).ToString();
    }

    private static unsafe string V6(byte* address, int offset)
    {
        Span<byte> bytes = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            bytes[i] = address[offset + i];
        }

        return new IPAddress(bytes).ToString();
    }

    private static int Port(uint networkOrderPort)
        => (ushort)IPAddress.NetworkToHostOrder((short)(networkOrderPort & 0xFFFF));

    private sealed class ConnState
    {
        public ConnState(int pid) => Pid = pid;

        public int Pid { get; }
        public long PrevIn { get; set; }
        public long PrevOut { get; set; }
        public long TotalIn { get; set; }
        public long TotalOut { get; set; }
        public double InRate { get; set; }
        public double OutRate { get; set; }
        public DateTime Seen { get; set; }
    }
}
