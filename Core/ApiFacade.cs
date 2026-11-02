using System;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Valve.Api
{
    /// <summary>Public API facade (ledger 19.01-19.06). Exposes engine capabilities to third-party code.
    /// Providers are wired by the client so the facade stays testable and does not bypass elevation checks.</summary>
    public static class ValveApi
    {
        public const string Version = "1.0.0";

        public static event Action<string>? Log;

        /// <summary>Snapshot provider wired from the running client (null = client not attached).</summary>
        public static Func<string>? SnapshotProvider { get; set; }

        /// <summary>Rule toggle provider (ruleId, enabled) -> result message.</summary>
        public static Func<string, bool, string>? RuleToggler { get; set; }

        /// <summary>Returns the current process snapshot or a diagnostic fallback.</summary>
        public static string GetActivitySnapshot()
        {
            Log?.Invoke("Snapshot requested");
            if (SnapshotProvider == null) return "client-not-attached";
            return SnapshotProvider();
        }

        /// <summary>Enables/disables the whole-machine blocker (19.01).</summary>
        public static string SetBlocked(bool blocked)
        {
            if (RuleToggler == null) return "client-not-attached";
            return RuleToggler("*blocker*", blocked);
        }

        public static bool IsAdministrator
        {
            get
            {
                try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        /// <summary>Elevation guard for write operations (19.05).</summary>
        public static bool EnsureElevation() => IsAdministrator;
    }
}
