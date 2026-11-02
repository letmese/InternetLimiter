using System.IO;
using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier2_BoundaryCornerCases;

public sealed class TornWriteCorruptStorageTests
{
    [TestCase(TestTier.Tier2, "Verifies AtomicStorage.IsValidPayload rejects null bytes and malformed JSON")]
    public void AtomicStorage_IsValidPayload_RejectsCorruptions()
    {
        TestAssert.IsFalse(AtomicStorage.IsValidPayload(null));
        TestAssert.IsFalse(AtomicStorage.IsValidPayload(""));
        TestAssert.IsFalse(AtomicStorage.IsValidPayload("   "));

        // All null bytes (torn write symptom)
        string nullBytes = new string('\0', 256);
        TestAssert.IsFalse(AtomicStorage.IsValidPayload(nullBytes));

        // Malformed JSON syntax within braces
        TestAssert.IsFalse(AtomicStorage.IsValidPayload("{\"incomplete\": }"));

        // Truncated JSON without closing brace or bracket
        TestAssert.IsFalse(AtomicStorage.IsValidPayload("{\"incomplete\": true"));
        TestAssert.IsFalse(AtomicStorage.IsValidPayload("{\"rules\": [{\"id\": 1}"));
        TestAssert.IsFalse(AtomicStorage.IsValidPayload("[{\"id\": 1}, {\"id\": 2"));
        TestAssert.IsFalse(AtomicStorage.IsValidPayload("{\"unclosed_string\": \"hello"));

        // Valid JSON
        TestAssert.IsTrue(AtomicStorage.IsValidPayload("{\"valid\": true}"));
        TestAssert.IsTrue(AtomicStorage.IsValidPayload("[1, 2, 3]"));
    }

    [TestCase(TestTier.Tier2, "Verifies AtomicStorage automatically heals and recovers from .bak when primary file is null-padded")]
    public void AtomicStorage_TornWriteNullBytes_AutoHealsFromBackup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-torn-" + Guid.NewGuid().ToString("N"));
        string targetFile = Path.Combine(tempDir, "state.json");
        string backupFile = targetFile + ".bak";

        try
        {
            Directory.CreateDirectory(tempDir);
            string validBackupData = "{\"lastValidState\": true, \"rules\": [1, 2]}";

            // Establish valid backup
            File.WriteAllText(backupFile, validBackupData);

            // Corrupt primary file with null bytes (simulating torn write on sudden power loss)
            File.WriteAllBytes(targetFile, new byte[512]);

            // LoadAtomic should detect corrupted primary, load .bak, self-heal primary, and report recoveredFromBackup = true
            string? recovered = AtomicStorage.LoadAtomic(targetFile, out bool recoveredFromBackup);

            TestAssert.IsTrue(recoveredFromBackup, "Expected recoveredFromBackup to be TRUE");
            TestAssert.AreEqual(validBackupData, recovered);

            // Verify primary was auto-healed on disk
            string healedContent = File.ReadAllText(targetFile);
            TestAssert.AreEqual(validBackupData, healedContent);

            // Verify .bak retained full payload and was NOT overwritten with null bytes
            TestAssert.IsTrue(File.Exists(backupFile), "Backup file must still exist after auto-heal");
            long backupLen = new FileInfo(backupFile).Length;
            TestAssert.IsTrue(backupLen > 0, $"Backup file must have non-zero length (was {backupLen} bytes)");
            TestAssert.AreEqual(validBackupData, File.ReadAllText(backupFile), "Backup file must retain intact valid payload");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [TestCase(TestTier.Tier2, "Verifies AtomicStorage heals from .bak when primary file is 0-byte truncated")]
    public void AtomicStorage_ZeroByteFile_AutoHealsFromBackup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-zerobyte-" + Guid.NewGuid().ToString("N"));
        string targetFile = Path.Combine(tempDir, "settings.json");
        string backupFile = targetFile + ".bak";

        try
        {
            Directory.CreateDirectory(tempDir);
            string validData = "{\"theme\": \"Dark\"}";
            File.WriteAllText(backupFile, validData);
            File.WriteAllBytes(targetFile, Array.Empty<byte>()); // 0 bytes

            string? recovered = AtomicStorage.LoadAtomic(targetFile, out bool recoveredFromBackup);

            TestAssert.IsTrue(recoveredFromBackup);
            TestAssert.AreEqual(validData, recovered);

            // Verify primary was auto-healed on disk
            TestAssert.AreEqual(validData, File.ReadAllText(targetFile), "Primary file must be auto-healed on disk");

            // Verify .bak retained full payload and was NOT overwritten with 0 bytes
            TestAssert.IsTrue(File.Exists(backupFile), "Backup file must still exist after auto-heal");
            long backupLen = new FileInfo(backupFile).Length;
            TestAssert.IsTrue(backupLen > 0, $"Backup file must have non-zero length (was {backupLen} bytes)");
            TestAssert.AreEqual(validData, File.ReadAllText(backupFile), "Backup file must retain intact valid payload");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [TestCase(TestTier.Tier2, "Verifies AtomicStorage detects truncated JSON lacking closing brace and auto-heals from .bak")]
    public void AtomicStorage_TruncatedJsonWithoutClosingBrace_AutoHealsFromBackup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-trunc-" + Guid.NewGuid().ToString("N"));
        string targetFile = Path.Combine(tempDir, "rules.json");
        string backupFile = targetFile + ".bak";

        try
        {
            Directory.CreateDirectory(tempDir);
            string validData = "{\"rules\": [{\"id\": 1, \"name\": \"BlockAll\"}], \"enabled\": true}";
            File.WriteAllText(backupFile, validData);

            // Write truncated JSON without closing brace
            string truncated = "{\"rules\": [{\"id\": 1, \"name\": \"BlockAll\"}], \"enabled\":";
            File.WriteAllText(targetFile, truncated);

            string? recovered = AtomicStorage.LoadAtomic(targetFile, out bool recoveredFromBackup);

            TestAssert.IsTrue(recoveredFromBackup, "Truncated JSON without closing brace must trigger auto-heal from .bak");
            TestAssert.AreEqual(validData, recovered, "Recovered data must match backup content");

            // Verify disk state
            TestAssert.AreEqual(validData, File.ReadAllText(targetFile), "Primary file must be auto-healed on disk");
            TestAssert.AreEqual(validData, File.ReadAllText(backupFile), "Backup file must retain intact valid content");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [TestCase(TestTier.Tier2, "Verifies repeated crashes after auto-heal still successfully recover from .bak")]
    public void AtomicStorage_RepeatedCorruption_SecondCrashRecoversFromBackup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "valve-test-repeat-" + Guid.NewGuid().ToString("N"));
        string targetFile = Path.Combine(tempDir, "settings.json");
        string backupFile = targetFile + ".bak";

        try
        {
            Directory.CreateDirectory(tempDir);
            string initialValid = "{\"theme\": \"Dark\", \"downloadLimit\": 5000}";
            AtomicStorage.SaveAtomic(targetFile, initialValid);

            // Verify initial state
            TestAssert.IsTrue(File.Exists(targetFile));
            TestAssert.IsTrue(File.Exists(backupFile));
            TestAssert.AreEqual(initialValid, File.ReadAllText(targetFile));
            TestAssert.AreEqual(initialValid, File.ReadAllText(backupFile));

            // Incident 1: Sudden power cut -> primary truncated to 0 bytes
            File.WriteAllBytes(targetFile, Array.Empty<byte>());

            string? recovered1 = AtomicStorage.LoadAtomic(targetFile, out bool recFlag1);
            TestAssert.IsTrue(recFlag1, "Incident 1: 0-byte file must recover from .bak");
            TestAssert.AreEqual(initialValid, recovered1);
            TestAssert.AreEqual(initialValid, File.ReadAllText(targetFile), "Incident 1: primary healed on disk");
            TestAssert.AreEqual(initialValid, File.ReadAllText(backupFile), "Incident 1: .bak must NOT be overwritten with 0 bytes");

            // Incident 2: Second sudden power cut -> torn write with 512 null bytes
            File.WriteAllBytes(targetFile, new byte[512]);

            string? recovered2 = AtomicStorage.LoadAtomic(targetFile, out bool recFlag2);
            TestAssert.IsTrue(recFlag2, "Incident 2: second corruption must still recover from .bak");
            TestAssert.AreEqual(initialValid, recovered2);
            TestAssert.AreEqual(initialValid, File.ReadAllText(targetFile), "Incident 2: primary healed on disk");
            TestAssert.AreEqual(initialValid, File.ReadAllText(backupFile), "Incident 2: .bak must retain valid content");

            // Incident 3: Third sudden power cut -> truncated JSON without closing brace
            File.WriteAllText(targetFile, "{\"theme\": \"Dark\", \"downloadLimit\":");

            string? recovered3 = AtomicStorage.LoadAtomic(targetFile, out bool recFlag3);
            TestAssert.IsTrue(recFlag3, "Incident 3: third corruption (truncated JSON) must still recover from .bak");
            TestAssert.AreEqual(initialValid, recovered3);
            TestAssert.AreEqual(initialValid, File.ReadAllText(targetFile), "Incident 3: primary healed on disk");
            TestAssert.AreEqual(initialValid, File.ReadAllText(backupFile), "Incident 3: .bak must retain valid content");
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
