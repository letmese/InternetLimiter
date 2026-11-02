using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class ValveIpcTests
{
    [TestCase(TestTier.Tier1, "Verifies SET_APP_LIMIT accepts JSON payloads and correctly parses Windows paths with colons (B3)")]
    public void ValveIpc_SetAppLimit_JsonPayload_ParsesPathNames()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-ipc-limit-" + Guid.NewGuid().ToString("N"));
        string testPipe = $"ValveIpcLimitTestPipe_{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(tempDir);
            using var host = new ValveServiceHost(tempDir, testPipe);
            Thread.Sleep(100);

            // Test 1: JSON payload with drive colon in path
            string fullPath = @"C:\Program Files\NetworkTool\app.exe";
            var payloadObj = new
            {
                app = fullPath,
                kbps = 4096,
                isDownload = true,
                enabled = true
            };
            string json = JsonSerializer.Serialize(payloadObj);
            string? reply = ValveIpcClient.SendCommand("SET_APP_LIMIT", json, timeoutMs: 2500, pipeName: testPipe);

            TestAssert.IsNotNull(reply, "Service must respond to JSON SET_APP_LIMIT");
            TestAssert.IsTrue(reply!.Contains("\"status\":\"ok\""), "SET_APP_LIMIT reply must be ok");

            // Test 2: Fallback colon-separated format with path containing colon
            string colonPayload = $"{fullPath}:2048:false:true";
            string? replyColon = ValveIpcClient.SendCommand("SET_APP_LIMIT", colonPayload, timeoutMs: 2500, pipeName: testPipe);
            TestAssert.IsNotNull(replyColon, "Service must respond to colon fallback SET_APP_LIMIT");
            TestAssert.IsTrue(replyColon!.Contains("\"status\":\"ok\""), "Colon fallback reply must be ok");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [TestCase(TestTier.Tier1, "Verifies SYNC_RULES applies per-app limits, priorities, and blocker state (B3)")]
    public void ValveIpc_SyncRules_AppliesAppLimitsAndBlockerState()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-ipc-sync-" + Guid.NewGuid().ToString("N"));
        string testPipe = $"ValveIpcSyncTestPipe_{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(tempDir);
            using var host = new ValveServiceHost(tempDir, testPipe);
            Thread.Sleep(100);

            var settings = new Settings
            {
                BlockerEnabled = true,
                DefaultBlockerAction = "Allow",
                LimiterEnabled = true,
                PrioritiesEnabled = true,
                Apps = new List<AppRule>
                {
                    new AppRule
                    {
                        App = "chrome.exe",
                        AppPath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                        DownloadLimitKbps = 1500,
                        DownloadLimitEnabled = true,
                        UploadLimitKbps = 500,
                        UploadLimitEnabled = true,
                        Priority = TrafficPriority.High
                    }
                }
            };

            string settingsJson = JsonSerializer.Serialize(settings);
            string? reply = ValveIpcClient.SendCommand("SYNC_RULES", settingsJson, timeoutMs: 2500, pipeName: testPipe);

            TestAssert.IsNotNull(reply, "Service must respond to SYNC_RULES");
            TestAssert.IsTrue(reply!.Contains("\"status\":\"ok\""), "SYNC_RULES reply must be ok");

            // Query STATUS to verify running state
            string? statusReply = ValveIpcClient.SendCommand("STATUS", null, timeoutMs: 2500, pipeName: testPipe);
            TestAssert.IsNotNull(statusReply, "Service must respond to STATUS");
            TestAssert.IsTrue(statusReply!.Contains("\"status\":\"ok\""), "STATUS reply must be ok");

            // Query STATS to verify IPC stats counters (F4)
            string? statsReply = ValveIpcClient.SendCommand("STATS", null, timeoutMs: 2500, pipeName: testPipe);
            TestAssert.IsNotNull(statsReply, "Service must respond to STATS");
            TestAssert.IsTrue(statsReply!.Contains("\"totalIn\":"), "STATS reply must contain totalIn");
            TestAssert.IsTrue(statsReply!.Contains("\"perPid\":"), "STATS reply must contain perPid");

            // Query HELD_PACKETS (F4)
            string? heldReply = ValveIpcClient.SendCommand("HELD_PACKETS", null, timeoutMs: 2500, pipeName: testPipe);
            TestAssert.IsNotNull(heldReply, "Service must respond to HELD_PACKETS");
            TestAssert.IsTrue(heldReply!.StartsWith("[") && heldReply.EndsWith("]"), "HELD_PACKETS must be a JSON array");

            // Test RESOLVE_PACKET (F4)
            string resolvePayload = JsonSerializer.Serialize(new WinDivertShaper.ResolvePacketPayload
            {
                packetId = 99999,
                allow = true,
                rememberFlow = true,
                durationSeconds = 0
            });
            string? resolveReply = ValveIpcClient.SendCommand("RESOLVE_PACKET", resolvePayload, timeoutMs: 2500, pipeName: testPipe);
            TestAssert.IsNotNull(resolveReply, "Service must respond to RESOLVE_PACKET");
            TestAssert.IsTrue(resolveReply!.Contains("\"status\":\"ok\""), "RESOLVE_PACKET reply must be ok");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
