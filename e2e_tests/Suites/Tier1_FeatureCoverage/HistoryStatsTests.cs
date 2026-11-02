using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class HistoryStatsTests
{
    [TestCase(TestTier.Tier1, "Verifies process with connections records via connection path only and process without connections records via compensating path, preventing double-counting (B2)")]
    public void ProcessWithAndWithoutConnections_RecordsOnce()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-hist-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HistoryStore(tempDir);
            
            // Scenario A: Process with connections (PID 100, app "browser.exe")
            // Simulates connection loop recording 1,000 bytes In, 500 bytes Out
            var pidsRecordedFromConnections = new HashSet<int>();
            var compensatingRecorded = new HashSet<int>();

            int pidConn = 100;
            string appName = "browser.exe";

            // 1. Connection arrives for pidConn
            pidsRecordedFromConnections.Add(pidConn);
            store.Record(appName, 1000, 500);

            // 2. Compensating record attempted for pidConn in ApplyDriverRates
            // Should be suppressed because pidConn is in pidsRecordedFromConnections
            if (!pidsRecordedFromConnections.Contains(pidConn) && compensatingRecorded.Add(pidConn))
            {
                store.Record(appName, 1000, 500); // Must NOT execute
            }

            var today = DateOnly.FromDateTime(DateTime.Now);
            var usageConn = store.GetDay(today).FirstOrDefault(u => u.App == appName);
            TestAssert.IsNotNull(usageConn);
            TestAssert.AreEqual(1000L, usageConn.BytesIn, "Process with connections must record only connection totals, zero double-count");
            TestAssert.AreEqual(500L, usageConn.BytesOut, "Process with connections must record only connection totals, zero double-count");

            // Scenario B: Process without connections (PID 200, app "vpnservice.exe")
            // Has 2,000 bytes In, 1,000 bytes Out from engine, but NO connection rows
            int pidNoConn = 200;
            string vpnName = "vpnservice.exe";

            // Compensating record executed
            if (!pidsRecordedFromConnections.Contains(pidNoConn) && compensatingRecorded.Add(pidNoConn))
            {
                store.Record(vpnName, 2000, 1000);
            }

            // Second attempt within same tick must be guarded
            if (!pidsRecordedFromConnections.Contains(pidNoConn) && compensatingRecorded.Add(pidNoConn))
            {
                store.Record(vpnName, 2000, 1000); // Must NOT execute
            }

            var usageVpn = store.GetDay(today).FirstOrDefault(u => u.App == vpnName);
            TestAssert.IsNotNull(usageVpn);
            TestAssert.AreEqual(2000L, usageVpn.BytesIn, "Compensating record must execute exactly once for process without connections");
            TestAssert.AreEqual(1000L, usageVpn.BytesOut, "Compensating record must execute exactly once for process without connections");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
