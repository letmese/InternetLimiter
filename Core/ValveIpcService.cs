using System.Security.AccessControl;
using System.Security.Principal;
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Valve.Core;

/// <summary>
/// IPC Message packet for Client-Service communication over local Named Pipe (NetLimiter Sec 28, Items 28.01, 28.02).
/// </summary>
public sealed class ValveIpcMessage
{
    public string Command { get; set; } = "";
    public string? Payload { get; set; }
}

/// <summary>
/// High-speed local Named Pipe IPC Server powering the dedicated Valve background service (Items 1.14, 28.01, 28.02).
/// Runs in ValveService / headless mode, allowing the GUI client to close while network shaping remains 100% active.
/// </summary>
public sealed class AppLimitPayload
{
    public string app { get; set; } = string.Empty;
    public int kbps { get; set; }
    public bool isDownload { get; set; } = true;
    public bool enabled { get; set; } = true;
}

public sealed class ValveIpcServer : IDisposable
{
    public const string DefaultPipeName = "ValveServicePipe";
    public string PipeName { get; }
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<ValveIpcMessage, string> _handler;
    private Task? _listenerTask;
    private bool _isDisposed;

    public ValveIpcServer(Func<ValveIpcMessage, string> handler, string? pipeName = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        PipeName = pipeName ?? DefaultPipeName;
    }

    public void Start()
    {
        _listenerTask = Task.Run(ListenLoop);
    }

    private static NamedPipeServerStream CreateNamedPipeServer(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var ps = new PipeSecurity();
                ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                try
                {
                    var cu = WindowsIdentity.GetCurrent().User;
                    if (cu != null) ps.AddAccessRule(new PipeAccessRule(cu, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                }
                catch { }
                return NamedPipeServerStreamAcl.Create(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0, 0, ps);
            }
            catch { }
        }
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested && !_isDisposed)
        {
            try
            {
#pragma warning disable CA1416
                using var pipe = CreateNamedPipeServer(PipeName);

                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, leaveOpen: true);

                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(line))
                {
                    var msg = JsonSerializer.Deserialize<ValveIpcMessage>(line);
                    string reply = msg != null ? _handler(msg) : "{\"status\":\"error\",\"message\":\"null_msg\"}";
                    await writer.WriteLineAsync(reply).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
#pragma warning restore CA1416
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Continue listening for next client
            }
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}

/// <summary>
/// Client interface communicating with the background service over local Named Pipe (Items 28.01.c, 28.02).
/// </summary>
public static class ValveIpcClient
{
    public const string DefaultPipeName = ValveIpcServer.DefaultPipeName;

    public static bool IsServiceRunning(int timeoutMs = 200, string? pipeName = null)
    {
        try
        {
#pragma warning disable CA1416
            using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.InOut);
            client.Connect(timeoutMs);
            return client.IsConnected;
#pragma warning restore CA1416
        }
        catch
        {
            return false;
        }
    }

    public static string? SendCommand(string command, string? payload = null, int timeoutMs = 1500, string? pipeName = null)
    {
        try
        {
#pragma warning disable CA1416
            using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.InOut);
            client.Connect(timeoutMs);
            if (!client.IsConnected) return null;

            using var writer = new StreamWriter(client, Encoding.UTF8, 1024, leaveOpen: true);
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

            var req = new ValveIpcMessage { Command = command, Payload = payload };
            string reqJson = JsonSerializer.Serialize(req);
            writer.WriteLine(reqJson);
            writer.Flush();

            return reader.ReadLine();
#pragma warning restore CA1416
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Headless Background Service Host (Items 1.14, 28.01, 28.02).
/// Hosts WinDivertShaper and Named Pipe server.
/// </summary>
public sealed class ValveServiceHost : IDisposable
{
    private readonly WinDivertShaper _shaper;
    private readonly SettingsStore _store;
    private readonly Settings _settings;
    private readonly ValveIpcServer _server;
    private readonly ManualResetEvent _stopSignal = new(false);

    public ValveServiceHost(string? rootDir = null, string? pipeName = null)
    {
        _store = new SettingsStore(rootDir);
        _settings = _store.Load();
        _shaper = new WinDivertShaper();

        _shaper.DefaultBlockerAction = _settings.DefaultBlockerAction;
        _shaper.SetUnifiedRules(_settings.Rules);
        _shaper.SetFilters(_settings.Filters);
        // Master switches default OFF at startup, matching GUI (B1)
        _shaper.IsLimiterEnabled = false;
        _shaper.IsPrioritiesEnabled = false;
        _shaper.IsBlockerEnabled = false;
        _shaper.GlobalDownloadLimitKbps = 0;
        _shaper.GlobalUploadLimitKbps = 0;

        // Configure limits from settings
        foreach (var app in _settings.Apps)
        {
            if (app.DownloadLimitKbps > 0 && app.DownloadLimitEnabled)
                _shaper.SetAppLimit(app.App, app.DownloadLimitKbps, isDownload: true, enabled: true);
            if (app.UploadLimitKbps > 0 && app.UploadLimitEnabled)
                _shaper.SetAppLimit(app.App, app.UploadLimitKbps, isDownload: false, enabled: true);
        }

        _shaper.Start();

        _server = new ValveIpcServer(HandleIpcRequest, pipeName);
        _server.Start();
    }

    private string HandleIpcRequest(ValveIpcMessage msg)
    {
        switch (msg.Command?.ToUpperInvariant())
        {
            case "PING":
                return "{\"status\":\"ok\",\"reply\":\"PONG\"}";

            case "STATUS":
                return JsonSerializer.Serialize(new
                {
                    status = "ok",
                    shaperAvailable = _shaper.IsAvailable,
                    activeRules = _settings.Rules.Count,
                    activeApps = _settings.Apps.Count
                });

            case "STATS":
                var statsPayload = _shaper.GetStatsSnapshot();
                return JsonSerializer.Serialize(statsPayload);

            case "HELD_PACKETS":
                var heldList = _shaper.GetHeldPacketsDto();
                return JsonSerializer.Serialize(heldList);

            case "RESOLVE_PACKET":
                if (!string.IsNullOrEmpty(msg.Payload))
                {
                    try
                    {
                        var res = JsonSerializer.Deserialize<WinDivertShaper.ResolvePacketPayload>(msg.Payload);
                        if (res != null)
                        {
                            _shaper.ReleaseHeldPacket(res.packetId, res.allow, res.rememberFlow);
                            return "{\"status\":\"ok\"}";
                        }
                    }
                    catch (Exception ex)
                    {
                        return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
                    }
                }
                return "{\"status\":\"error\",\"message\":\"missing_payload\"}";

            case "SYNC_RULES":
                if (!string.IsNullOrEmpty(msg.Payload))
                {
                    var updated = JsonSerializer.Deserialize<Settings>(msg.Payload);
                    if (updated != null)
                    {
                        _shaper.SetUnifiedRules(updated.Rules ?? new List<UnifiedRule>());
                        _shaper.SetFilters(updated.Filters ?? new List<FilterRule>());
                        _shaper.IsBlockerEnabled = updated.BlockerEnabled;
                        _shaper.DefaultBlockerAction = updated.DefaultBlockerAction;
                        _shaper.IsLimiterEnabled = updated.LimiterEnabled;
                        _shaper.IsPrioritiesEnabled = updated.PrioritiesEnabled;
                        _shaper.GlobalDownloadLimitKbps = updated.GlobalDownloadLimitEnabled ? updated.GlobalDownloadLimitKbps : 0;
                        _shaper.GlobalUploadLimitKbps = updated.GlobalUploadLimitEnabled ? updated.GlobalUploadLimitKbps : 0;

                        if (updated.Apps != null)
                        {
                            foreach (var app in updated.Apps)
                            {
                                string key = !string.IsNullOrEmpty(app.AppPath) ? app.AppPath : app.App;
                                if (!string.IsNullOrEmpty(key))
                                {
                                    _shaper.SetAppLimit(key, app.DownloadLimitKbps, isDownload: true, app.DownloadLimitEnabled);
                                    _shaper.SetAppLimit(key, app.UploadLimitKbps, isDownload: false, app.UploadLimitEnabled);
                                    _shaper.SetAppPriority(key, app.Priority);
                                }
                            }
                        }

                        _store.Save(updated);
                    }
                }
                return "{\"status\":\"ok\"}";

            case "SET_APP_LIMIT":
                if (!string.IsNullOrEmpty(msg.Payload))
                {
                    bool handled = false;
                    try
                    {
                        var data = JsonSerializer.Deserialize<AppLimitPayload>(msg.Payload);
                        if (data != null && !string.IsNullOrEmpty(data.app))
                        {
                            _shaper.SetAppLimit(data.app, data.kbps, data.isDownload, data.enabled);
                            handled = true;
                        }
                    }
                    catch { }

                    if (!handled)
                    {
                        // Fallback: colon parsing (handling paths with drive colons correctly)
                        var parts = msg.Payload.Split(':');
                        if (parts.Length >= 4)
                        {
                            string app = string.Join(":", parts.Take(parts.Length - 3));
                            if (int.TryParse(parts[^3], out int kbps) &&
                                bool.TryParse(parts[^2], out bool isDl) &&
                                bool.TryParse(parts[^1], out bool en))
                            {
                                _shaper.SetAppLimit(app, kbps, isDl, en);
                            }
                        }
                    }
                }
                return "{\"status\":\"ok\"}";

            case "STOP":
                _stopSignal.Set();
                return "{\"status\":\"stopping\"}";

            default:
                return "{\"status\":\"unknown_command\"}";
        }
    }

    public void Run()
    {
        _stopSignal.WaitOne();
    }

    public void Stop()
    {
        _stopSignal.Set();
    }

    public void Dispose()
    {
        _server.Dispose();
        _shaper.Dispose();
        _stopSignal.Dispose();
    }
}
