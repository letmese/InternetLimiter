using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Valve.Core
{
    /// <summary>JSON-over-TCP remote administration (ledger 15.01-15.09).
    /// Server listens on a configurable port (default 4045), authenticates with a shared token,
    /// and answers commands through a client-supplied executor. Client supports host lists.</summary>
    public static class RemoteAdmin
    {
        public sealed record Command(string Type, string Arg1, string Arg2);

        public static Func<string, string, string, string>? Executor { get; set; }

        public static string ExecuteLocal(string type, string arg1 = "", string arg2 = "")
        {
            return Executor == null ? "{\"ok\":false,\"msg\":\"executor-not-attached\"}"
                : Executor(type, arg1, arg2) ?? "{\"ok\":false,\"msg\":\"null\"}";
        }

        // ---------- server ----------
        private static TcpListener? _listener;
        private static CancellationTokenSource? _cts;

        public static bool StartServer(int port, string token, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrWhiteSpace(token)) { error = "token-required"; return false; }
                if (_listener != null) { error = "already-listening"; return false; }
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ServerLoop(_listener, token, _cts.Token));
                return true;
            }
            catch (Exception ex) { _listener = null; error = ex.Message; return false; }
        }

        public static void StopServer()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        private static async Task ServerLoop(TcpListener listener, string token, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch { break; }
                _ = Task.Run(() => HandleClient(client, token));
            }
        }

        private static async Task HandleClient(TcpClient client, string token)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                    string? line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) return;
                    var cmd = JsonSerializer.Deserialize<Command>(line);
                    if (cmd == null) { await writer.WriteLineAsync("{\"ok\":false,\"msg\":\"bad-json\"}"); return; }
                    if (cmd.Type == "PING") { await writer.WriteLineAsync("{\"ok\":true,\"msg\":\"pong\",\"v\":\"" + Valve.Api.ValveApi.Version + "\"}"); return; }
                    if (cmd.Type != "AUTH") { await writer.WriteLineAsync("{\"ok\":false,\"msg\":\"auth-required\"}"); return; }
                    if (string.IsNullOrWhiteSpace(token) || cmd.Arg1 != token) { await writer.WriteLineAsync("{\"ok\":false,\"msg\":\"bad-token\"}"); return; }
                    // Arg2 carries the inner command as JSON (Type/Arg1/Arg2)
                    Command? inner = null;
                    try { inner = JsonSerializer.Deserialize<Command>(cmd.Arg2 ?? ""); } catch { }
                    if (inner == null) { await writer.WriteLineAsync("{\"ok\":false,\"msg\":\"bad-command\"}"); return; }
                    string resp = ExecuteLocal(inner.Type, inner.Arg1 ?? "", inner.Arg2 ?? "");
                    await writer.WriteLineAsync(resp);
                }
                catch { }
            }
        }

        // ---------- client ----------
        public static string SendCommand(string host, int port, string token, string type, string arg1 = "", string arg2 = "")
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(host, port);
                client.ReceiveTimeout = 5000;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                var inner = new Command(type, arg1, arg2);
                var auth = new Command("AUTH", token, JsonSerializer.Serialize(inner));
                writer.WriteLine(JsonSerializer.Serialize(auth));
                string? r1 = reader.ReadLine();
                return r1 ?? "{\"ok\":false,\"msg\":\"no-response\"}";
            }
            catch (Exception ex) { return "{\"ok\":false,\"msg\":\"" + ex.Message.Replace("\"", "'") + "\"}"; }
        }
    }
}
