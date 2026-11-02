using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Valve.Core
{
    /// <summary>Event Viewer integration (ledger 28.17): writes to the Windows "Valve" application log,
    /// plus diagnostic report export. Log source creation requires elevation and is guarded.</summary>
    public static class EventLogBridge
    {
        public const string Source = "Valve";
        private static bool _registered;

        public static void Init()
        {
            try
            {
                if (!EventLog.SourceExists(Source))
                {
                    try { EventLog.CreateEventSource(Source, "Application"); }
                    catch { _registered = false; return; }
                }
                _registered = true;
            }
            catch { _registered = false; }
        }

        public static void Info(string message) => Write(EventLogEntryType.Information, message);
        public static void Warn(string message) => Write(EventLogEntryType.Warning, message);
        public static void Error(string message) => Write(EventLogEntryType.Error, message);

        private static void Write(EventLogEntryType kind, string message)
        {
            try
            {
                if (_registered) EventLog.WriteEntry(Source, message, kind);
            }
            catch { }
        }

        /// <summary>Exports a combined diagnostics dump (report + recent log text) to the given path.</summary>
        public static string ExportDiagnosticsReport(string destPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== VALVE EVENT LOG DUMP ===");
            sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            if (_registered)
            {
                try
                {
                    var log = new EventLog("Application");
                    int count = 0;
                    foreach (EventLogEntry e in log.Entries)
                    {
                        if (e.Source != Source) continue;
                        sb.AppendLine($"{e.TimeGenerated:yyyy-MM-dd HH:mm:ss} [{e.EntryType}] {e.Message}");
                        if (++count >= 200) break;
                    }
                }
                catch { sb.AppendLine("(event log read requires elevation)"); }
            }
            File.WriteAllText(destPath, sb.ToString());
            return destPath;
        }
    }
}
