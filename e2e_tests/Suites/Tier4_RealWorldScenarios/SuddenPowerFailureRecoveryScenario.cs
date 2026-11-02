using System.IO;
using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier4_RealWorldScenarios;

public sealed class SuddenPowerFailureRecoveryScenario
{
    [TestCase(TestTier.Tier4, "Simulates sudden machine power failure during file save and verifies full automatic state recovery from .bak")]
    public void SuddenPowerFailure_InterruptedWrite_AutoHealsFromBackup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-powerfail-" + Guid.NewGuid().ToString("N"));
        string targetFile = Path.Combine(tempDir, "valve_rules.json");
        string backupFile = targetFile + ".bak";
        string strandedTmpFile = targetFile + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            Directory.CreateDirectory(tempDir);

            // Step 1: Normal operational state prior to crash
            string intactState = "{\"version\": 5, \"rules\": [{\"name\": \"BlockMalware\", \"blocked\": true}]}";
            File.WriteAllText(backupFile, intactState);

            // Step 2: Simulate power outage mid-flush: primary file has partial/null bytes, stranded .tmp file left behind
            File.WriteAllBytes(targetFile, new byte[1024]); // 1KB of null bytes from torn sectors
            File.WriteAllText(strandedTmpFile, "{\"incomplete_broken_json\": true");

            TestAssert.IsTrue(File.Exists(targetFile));
            TestAssert.IsTrue(File.Exists(strandedTmpFile));

            // Step 3: Application restarts after crash and attempts to load configuration
            string? loadedState = AtomicStorage.LoadAtomic(targetFile, out bool recoveredFromBackup);

            // Step 4: Verify recovery
            TestAssert.IsTrue(recoveredFromBackup, "Expected AtomicStorage to recover from backup after simulated power outage");
            TestAssert.AreEqual(intactState, loadedState);

            // Verify primary file was healed on disk with intact state
            string primaryOnDisk = File.ReadAllText(targetFile);
            TestAssert.AreEqual(intactState, primaryOnDisk);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [TestCase(TestTier.Tier4, "Simulates repeated power blackouts across auto-heal cycles and verifies continuous zero-data-loss recovery")]
    public void SuddenPowerFailure_RepeatedBlackout_BackupRetainedAndHealed()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-multi-blackout-" + Guid.NewGuid().ToString("N"));
        string targetFile = Path.Combine(tempDir, "valve_rules.json");
        string backupFile = targetFile + ".bak";

        try
        {
            Directory.CreateDirectory(tempDir);
            string goldenState = "{\"version\": 5, \"rules\": [{\"name\": \"BlockMalware\", \"blocked\": true}], \"shaper\": {\"rate\": 100000}}";
            AtomicStorage.SaveAtomic(targetFile, goldenState);

            // Blackout Cycle 1: Machine loses power mid-flush -> 0-byte file
            File.WriteAllBytes(targetFile, Array.Empty<byte>());
            string? load1 = AtomicStorage.LoadAtomic(targetFile, out bool healed1);
            TestAssert.IsTrue(healed1, "Cycle 1: Auto-heal triggered from backup");
            TestAssert.AreEqual(goldenState, load1);
            TestAssert.AreEqual(goldenState, File.ReadAllText(backupFile), "Cycle 1: Backup must retain full payload");

            // Blackout Cycle 2: Immediate power loss on reboot -> torn sector with 2048 null bytes
            File.WriteAllBytes(targetFile, new byte[2048]);
            string? load2 = AtomicStorage.LoadAtomic(targetFile, out bool healed2);
            TestAssert.IsTrue(healed2, "Cycle 2: Auto-heal triggered from backup");
            TestAssert.AreEqual(goldenState, load2);
            TestAssert.AreEqual(goldenState, File.ReadAllText(backupFile), "Cycle 2: Backup must retain full payload");

            // Blackout Cycle 3: Power loss during JSON write -> truncated payload
            File.WriteAllText(targetFile, "{\"version\": 5, \"rules\": [{\"name\": \"BlockMalware\"");
            string? load3 = AtomicStorage.LoadAtomic(targetFile, out bool healed3);
            TestAssert.IsTrue(healed3, "Cycle 3: Auto-heal triggered from backup");
            TestAssert.AreEqual(goldenState, load3);
            TestAssert.AreEqual(goldenState, File.ReadAllText(backupFile), "Cycle 3: Backup must retain full payload");
            TestAssert.AreEqual(goldenState, File.ReadAllText(targetFile), "Cycle 3: Primary healed on disk");
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
