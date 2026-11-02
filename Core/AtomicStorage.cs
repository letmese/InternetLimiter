using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Valve.Core;

/// <summary>
/// Crash-resilient, power-failure-proof file persistence.
/// Protects against unexpected electricity outages, hardware power cuts, and torn writes by:
/// 1. Writing to an isolated temp file using FileOptions.WriteThrough (bypassing volatile OS cache).
/// 2. Explicitly flushing physical storage hardware buffers via FileStream.Flush(flushToDisk: true).
/// 3. Safely retaining an intact rolling backup (.bak) of the previous valid state.
/// 4. Atomically replacing the target file using Win32 ReplaceFile with REPLACEFILE_WRITE_THROUGH.
/// 5. Validating content integrity on read (checking for 0-byte truncation, null-byte pads from torn writes, and JSON syntax).
/// 6. Automatically falling back to and self-healing from the .bak backup if the primary file is corrupted by sudden power loss,
///    strictly guaranteeing that the valid .bak file is never overwritten by corrupted data.
/// </summary>
public static class AtomicStorage
{
    private const uint REPLACEFILE_WRITE_THROUGH = 0x00000001;
    private const uint REPLACEFILE_IGNORE_MERGE_ERRORS = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFile(
        string lpReplacedFileName,
        string lpReplacementFileName,
        string? lpBackupFileName,
        uint dwReplaceFlags,
        IntPtr lpExclude,
        IntPtr lpReserved);

    /// <summary>
    /// Writes content to disk atomically with hardware write-through and maintains a verified .bak backup.
    /// </summary>
    /// <param name="targetPath">Target file path.</param>
    /// <param name="content">UTF-8 text payload to store.</param>
    /// <param name="createBackup">Whether to update the .bak backup from the existing file. Defaults to true.
    /// Should be set to false when auto-healing from a backup or when restoring corrupted state.</param>
    public static void SaveAtomic(string targetPath, string content, bool createBackup = true)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string backupPath = targetPath + ".bak";

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);

            // Step 1: Write to temp file with FileOptions.WriteThrough and explicit flush to non-volatile disk
            using (var fs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            // Step 2: Atomic replacement
            if (File.Exists(targetPath))
            {
                // Verify whether the existing target file is valid before using it as a backup.
                // If targetPath is corrupted (0 bytes, null padded, truncated JSON) or if backup
                // creation is disabled (e.g. during auto-heal), we MUST NEVER overwrite backupPath!
                bool targetIsValid = false;
                try
                {
                    if (createBackup && new FileInfo(targetPath).Length > 0)
                    {
                        string existing = File.ReadAllText(targetPath, Encoding.UTF8);
                        if (IsValidPayload(existing))
                        {
                            targetIsValid = true;
                            File.Copy(targetPath, backupPath, overwrite: true);
                        }
                    }
                }
                catch
                {
                    // Non-fatal if backup copy fails
                }

                // Win32 ReplaceFile provides atomic directory entry replacement on NTFS/ReFS.
                // Pass targetIsValid ? backupPath : null so ReplaceFile NEVER moves a corrupted target file into backupPath!
                bool replaced = ReplaceFile(
                    targetPath,
                    tempPath,
                    targetIsValid ? backupPath : null,
                    REPLACEFILE_WRITE_THROUGH | REPLACEFILE_IGNORE_MERGE_ERRORS,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (!replaced)
                {
                    // Fallback to File.Move if ReplaceFile is not supported on this volume.
                    // Note: If targetIsValid == false, backupPath is not touched.
                    File.Move(tempPath, targetPath, overwrite: true);
                }

                // After replacement, if File.Exists(targetPath) && !File.Exists(backupPath) && createBackup,
                // copy targetPath to backupPath to ensure a valid baseline backup exists.
                if (createBackup && File.Exists(targetPath) && !File.Exists(backupPath))
                {
                    try
                    {
                        File.Copy(targetPath, backupPath, overwrite: true);
                    }
                    catch { }
                }
            }
            else
            {
                // Target does not exist yet; simple move is atomic in NTFS within the same directory
                File.Move(tempPath, targetPath, overwrite: true);

                // Create initial backup only if requested and backup does not already exist
                if (createBackup && !File.Exists(backupPath))
                {
                    try
                    {
                        File.Copy(targetPath, backupPath, overwrite: true);
                    }
                    catch { }
                }
            }
        }
        finally
        {
            // Clean up any remaining temp file
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Reads and verifies file content. If the primary file is missing, empty, or corrupted
    /// (e.g. from an abrupt power loss or torn write), automatically recovers from the .bak backup.
    /// Guarantees that auto-healing never overwrites or destroys the valid backup file.
    /// </summary>
    public static string? LoadAtomic(string targetPath, out bool recoveredFromBackup)
    {
        recoveredFromBackup = false;
        string backupPath = targetPath + ".bak";

        // 1. Try reading primary file
        if (File.Exists(targetPath))
        {
            try
            {
                string text = File.ReadAllText(targetPath, Encoding.UTF8);
                if (IsValidPayload(text))
                {
                    return text;
                }
            }
            catch
            {
                // Primary file read error or corrupted
            }
        }

        // 2. Primary was corrupted or missing: try recovering from backup!
        if (File.Exists(backupPath))
        {
            try
            {
                string backupText = File.ReadAllText(backupPath, Encoding.UTF8);
                if (IsValidPayload(backupText))
                {
                    // Auto-heal: restore primary file from backup without destroying the backup
                    try
                    {
                        SaveAtomic(targetPath, backupText, createBackup: false);
                    }
                    catch { }

                    recoveredFromBackup = true;
                    return backupText;
                }
            }
            catch
            {
            }
        }

        // 3. Check if any stranded .tmp file from an interrupted write has valid content
        string? dir = Path.GetDirectoryName(targetPath);
        string filename = Path.GetFileName(targetPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try
            {
                string[] tempFiles = Directory.GetFiles(dir, filename + ".*.tmp");
                foreach (string tmp in tempFiles)
                {
                    try
                    {
                        string tmpText = File.ReadAllText(tmp, Encoding.UTF8);
                        if (IsValidPayload(tmpText))
                        {
                            SaveAtomic(targetPath, tmpText, createBackup: false);
                            recoveredFromBackup = true;
                            return tmpText;
                        }
                    }
                    catch { }
                    finally
                    {
                        try { File.Delete(tmp); } catch { }
                    }
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Validates that payload is non-empty, not all null bytes (torn write symptom), and valid JSON if applicable.
    /// Rejects truncated JSON without closing braces.
    /// </summary>
    public static bool IsValidPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Power outage torn writes often write zero-bytes / null padding across disk sectors
        if (text.Contains('\0'))
        {
            return false;
        }

        // If it starts with JSON structure characters, ensure it is syntactically valid JSON.
        // A truncated JSON file starting with '{' or '[' must NOT pass validation.
        string trimmed = text.Trim().Trim('\uFEFF');
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }
}
