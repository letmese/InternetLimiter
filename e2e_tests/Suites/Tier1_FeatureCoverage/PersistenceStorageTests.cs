using System.IO;
using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class PersistenceStorageTests
{
    [TestCase(TestTier.Tier1, "Verifies AtomicStorage saves and loads data with byte integrity")]
    public void AtomicStorage_SaveAndLoad_RoundTrip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-atomic-" + Guid.NewGuid().ToString("N"));
        string targetFile = Path.Combine(tempDir, "config.json");

        try
        {
            string payload = "{\"version\": 1, \"enabled\": true, \"appName\": \"Valve\"}";
            AtomicStorage.SaveAtomic(targetFile, payload);

            TestAssert.IsTrue(File.Exists(targetFile), "Target file should exist on disk");
            string? loaded = AtomicStorage.LoadAtomic(targetFile, out bool recovered);

            TestAssert.AreEqual(payload, loaded);
            TestAssert.IsFalse(recovered, "Should load directly from primary file without backup recovery");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [TestCase(TestTier.Tier1, "Verifies AtomicStorage creates .bak file on subsequent overwrites")]
    public void AtomicStorage_Overwrite_CreatesBackupFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-bak-" + Guid.NewGuid().ToString("N"));
        string targetFile = Path.Combine(tempDir, "settings.json");
        string backupFile = targetFile + ".bak";

        try
        {
            AtomicStorage.SaveAtomic(targetFile, "{\"v\": 1}");
            TestAssert.IsTrue(File.Exists(targetFile));

            // Overwrite with version 2
            AtomicStorage.SaveAtomic(targetFile, "{\"v\": 2}");
            TestAssert.IsTrue(File.Exists(backupFile), "Backup file .bak should exist after overwrite");

            string current = File.ReadAllText(targetFile);
            string backup = File.ReadAllText(backupFile);

            TestAssert.AreEqual("{\"v\": 2}", current);
            TestAssert.AreEqual("{\"v\": 1}", backup);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [TestCase(TestTier.Tier1, "Verifies SettingsStore round-trip saves and loads AppRule, ScheduleRule, and FilterRule")]
    public void SettingsStore_RoundTrip_PersistsAllEntities()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-settings-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new SettingsStore(tempDir);
            var settings = new Settings();

            settings.Apps.Add(new AppRule
            {
                App = "steam.exe",
                AppPath = @"C:\Games\steam.exe",
                UploadLimitKbps = 256,
                DownloadLimitKbps = 1024,
                Blocked = true,
                Priority = TrafficPriority.High
            });

            settings.Schedules.Add(new ScheduleRule
            {
                App = "steam.exe",
                LimitKbps = 500,
                StartMinute = 600,
                EndMinute = 1200
            });

            settings.Filters.Add(new FilterRule("Block Telnet", FilterConditionType.PortRange, "23")
            {
                Blocked = true
            });

            store.Save(settings);

            // Reload using fresh store instance
            var reloaded = new SettingsStore(tempDir).Load();

            TestAssert.AreEqual(1, reloaded.Apps.Count);
            TestAssert.AreEqual("steam.exe", reloaded.Apps[0].App);
            TestAssert.AreEqual(256, reloaded.Apps[0].UploadLimitKbps);
            TestAssert.AreEqual(1024, reloaded.Apps[0].DownloadLimitKbps);
            TestAssert.IsTrue(reloaded.Apps[0].Blocked);
            TestAssert.AreEqual(TrafficPriority.High, reloaded.Apps[0].Priority);

            TestAssert.AreEqual(1, reloaded.Schedules.Count);
            TestAssert.AreEqual(500, reloaded.Schedules[0].LimitKbps);

            TestAssert.AreEqual(1, reloaded.Filters.Count);
            TestAssert.AreEqual("Block Telnet", reloaded.Filters[0].Name);
            TestAssert.IsTrue(reloaded.Filters[0].Blocked);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
