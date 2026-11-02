using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Valve.Core;

/// <summary>
/// User-mode side of the Valve kernel driver.
///
/// The driver exposes \Device\Valve with a DOS symlink, so it opens like a file.
/// IOCTL codes must stay in lockstep with driver/valve.h — they are recomputed
/// here from the same CTL_CODE formula rather than hard-coded, so a change on one
/// side is obvious.
/// </summary>
public sealed class DriverClient : IDisposable
{
    private const uint FileDeviceUnknown = 0x00000022;
    private const uint MethodBuffered = 0;
    private const uint FileAnyAccess = 0;

    private static uint CtlCode(uint function)
        => (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (function << 2) | MethodBuffered;

    private static readonly uint IoctlSetLimit = CtlCode(0x800);
    private static readonly uint IoctlClearLimit = CtlCode(0x801);
    private static readonly uint IoctlQueryStats = CtlCode(0x802);
    private static readonly uint IoctlVersion = CtlCode(0x803);
    private static readonly uint IoctlSetEnabled = CtlCode(0x804);

    [StructLayout(LayoutKind.Sequential)]
    private struct LimitRequest
    {
        public uint ProcessId;
        public uint UploadBytesPerSecond;
        public uint DownloadBytesPerSecond;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatsReply
    {
        public uint ProcessId;
        public ulong BytesIn;
        public ulong BytesOut;
        public ulong DroppedIn;
        public ulong DroppedOut;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    private SafeFileHandle? _handle;

    public bool IsAvailable => _handle is { IsInvalid: false, IsClosed: false };
    public string? LastError { get; private set; }

    /// <summary>
    /// Opens the driver. Returns false (with LastError set) when it is not loaded,
    /// which is the normal case when the user has not installed it.
    /// </summary>
    public bool TryOpen()
    {
        if (IsAvailable)
        {
            return true;
        }

        _handle = CreateFileW(
            @"\\.\Valve",
            GenericRead | GenericWrite,
            0,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            LastError = Marshal.GetLastWin32Error() switch
            {
                2 => "driver not loaded",
                5 => "access denied - Valve must run as administrator",
                var code => $"CreateFile failed ({code})",
            };
            _handle = null;
            return false;
        }

        LastError = null;
        return true;
    }

    public bool SetEnabled(bool enabled)
    {
        if (!IsAvailable)
        {
            return false;
        }

        byte value = enabled ? (byte)1 : (byte)0;
        IntPtr buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, value);
            return DeviceIoControl(_handle!, IoctlSetEnabled, buffer, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Limits in bytes per second. Zero means unlimited in that direction.</summary>
    public bool SetLimit(int processId, uint uploadBytesPerSecond, uint downloadBytesPerSecond)
    {
        if (!IsAvailable)
        {
            return false;
        }

        var request = new LimitRequest
        {
            ProcessId = (uint)processId,
            UploadBytesPerSecond = uploadBytesPerSecond,
            DownloadBytesPerSecond = downloadBytesPerSecond,
        };

        int size = Marshal.SizeOf<LimitRequest>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(request, buffer, false);
            return DeviceIoControl(_handle!, IoctlSetLimit, buffer, (uint)size, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool ClearLimit(int processId)
    {
        if (!IsAvailable)
        {
            return false;
        }

        IntPtr buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, processId);
            return DeviceIoControl(_handle!, IoctlClearLimit, buffer, sizeof(uint), IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool TryGetVersion(out uint version)
    {
        version = 0;
        if (!IsAvailable)
        {
            return false;
        }

        IntPtr buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            if (!DeviceIoControl(_handle!, IoctlVersion, IntPtr.Zero, 0, buffer, sizeof(uint), out _, IntPtr.Zero))
            {
                return false;
            }

            version = (uint)Marshal.ReadInt32(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool TryGetStats(int processId, out ulong bytesIn, out ulong bytesOut, out ulong droppedIn, out ulong droppedOut)
    {
        bytesIn = bytesOut = droppedIn = droppedOut = 0;
        if (!IsAvailable)
        {
            return false;
        }

        int size = Math.Max(sizeof(uint), Marshal.SizeOf<StatsReply>());
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, processId);
            if (!DeviceIoControl(_handle!, IoctlQueryStats, buffer, sizeof(uint), buffer, (uint)size, out uint returned, IntPtr.Zero)
                || returned < Marshal.SizeOf<StatsReply>())
            {
                return false;
            }

            var reply = Marshal.PtrToStructure<StatsReply>(buffer);
            bytesIn = reply.BytesIn;
            bytesOut = reply.BytesOut;
            droppedIn = reply.DroppedIn;
            droppedOut = reply.DroppedOut;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
