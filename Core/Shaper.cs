using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Valve.Core;

/// <summary>
/// Relative importance Valve asks Windows to give an application's traffic.
/// The shaper implements this with DSCP marking, which only takes effect when the
/// packets cross a network that honours the markings.
/// </summary>
public enum TrafficPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

/// <summary>Outcome of a shaper call. Failures are reported, never thrown.</summary>
public readonly record struct ShaperResult(bool Ok, string Message);

public interface IShaper
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    Task<ShaperResult> SetUploadLimitAsync(string exePath, int kbps);

    Task<ShaperResult> SetPriorityAsync(string exePath, TrafficPriority priority);

    Task<ShaperResult> ClearAsync(string exePath);

    Task<ShaperResult> ClearAllAsync();

    Task<IReadOnlyDictionary<string, int>> GetActiveLimitsAsync();

    Task<IReadOnlyDictionary<string, TrafficPriority>> GetActivePrioritiesAsync();
}

/// <summary>
/// Upload shaping built on Windows Policy-based QoS.
/// <para>
/// Every change is a <c>New-NetQosPolicy</c> / <c>Remove-NetQosPolicy</c> pair run
/// through <c>powershell.exe</c> against the <c>ActiveStore</c>, so a policy takes
/// effect immediately and disappears on reboot. Policies are named after the
/// executable, which makes the reverse lookup (policy to process row) trivial:
/// <c>valve:chrome.exe</c> caps, <c>valve-pri:chrome.exe</c> marks priority.
/// </para>
/// <para>
/// Changing QoS policy requires an elevated process; a non-elevated call fails
/// with an access-denied message rather than throwing.
/// </para>
/// </summary>
public sealed class QosShaper : IShaper
{
    private const string LimitPrefix = "valve:";
    private const string PriorityPrefix = "valve-pri:";
    private const int TimeoutMs = 25_000;

    /// <summary>
    /// PowerShell 5.1 writes to a redirected pipe using the console code page, which
    /// mangles non-ASCII executable names. Force UTF-8 on both ends before anything
    /// else runs.
    /// </summary>
    private const string Preamble =
        "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }; ";

    private static readonly char[] JsonStarts = { '[', '{' };

    private readonly object _gate = new();
    private bool _probed;
    private bool _available;
    private string? _reason;

    public QosShaper()
    {
        // The probe spawns a process, and IsAvailable is read synchronously from the
        // UI thread. Warm the answer up in the background so the first read is instant.
        _ = Task.Run(Probe);
    }

    public bool IsAvailable
    {
        get
        {
            Probe();
            return _available;
        }
    }

    public string? UnavailableReason
    {
        get
        {
            Probe();
            return _available ? null : _reason;
        }
    }

    public async Task<ShaperResult> SetUploadLimitAsync(string exePath, int kbps)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new ShaperResult(false, "No executable path.");
        }

        // A cap of zero (or less) means "no cap", which is the same as clearing.
        if (kbps <= 0)
        {
            return await ClearAsync(exePath).ConfigureAwait(false);
        }

        string name = LimitPrefix + FileNameOf(exePath);
        long bits = (long)kbps * 1000L;

        // Remove first so re-capping an app replaces the old rate instead of failing
        // with "a policy with this name already exists".
        string script =
            Preamble +
            "$n = " + PsQuote(name) + "; " +
            "Remove-NetQosPolicy -Name $n -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue; " +
            "New-NetQosPolicy -Name $n -AppPathNameMatchCondition " + PsQuote(exePath) +
            " -ThrottleRateActionBitsPerSecond " + bits.ToString(CultureInfo.InvariantCulture) +
            " -PolicyStore ActiveStore -ErrorAction Stop | Out-Null";

        PsOutcome outcome = await Task.Run(() => RunPowerShell(script)).ConfigureAwait(false);
        if (!Succeeded(outcome))
        {
            return new ShaperResult(false, DescribeFailure(outcome));
        }

        return new ShaperResult(true, $"capped {FileNameOf(exePath)} to {kbps} kbit/s");
    }

    public async Task<ShaperResult> SetPriorityAsync(string exePath, TrafficPriority priority)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new ShaperResult(false, "No executable path.");
        }

        string name = PriorityPrefix + FileNameOf(exePath);
        int dscp = DscpFor(priority);

        string script =
            Preamble +
            "$n = " + PsQuote(name) + "; " +
            "Remove-NetQosPolicy -Name $n -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue; " +
            "New-NetQosPolicy -Name $n -AppPathNameMatchCondition " + PsQuote(exePath) +
            " -DSCPAction " + dscp.ToString(CultureInfo.InvariantCulture) +
            " -PolicyStore ActiveStore -ErrorAction Stop | Out-Null";

        PsOutcome outcome = await Task.Run(() => RunPowerShell(script)).ConfigureAwait(false);
        if (!Succeeded(outcome))
        {
            return new ShaperResult(false, DescribeFailure(outcome));
        }

        return new ShaperResult(true, $"set {FileNameOf(exePath)} priority to {priority} (DSCP {dscp})");
    }

    public async Task<ShaperResult> ClearAsync(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new ShaperResult(false, "No executable path.");
        }

        string file = FileNameOf(exePath);

        // Both policies are removed unconditionally; a missing policy is not an error.
        string script =
            Preamble +
            "Remove-NetQosPolicy -Name " + PsQuote(LimitPrefix + file) +
            " -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue; " +
            "Remove-NetQosPolicy -Name " + PsQuote(PriorityPrefix + file) +
            " -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue; " +
            "'ok'";

        PsOutcome outcome = await Task.Run(() => RunPowerShell(script)).ConfigureAwait(false);
        if (!Succeeded(outcome))
        {
            return new ShaperResult(false, DescribeFailure(outcome));
        }

        return new ShaperResult(true, $"cleared limit for {file}");
    }

    public async Task<ShaperResult> ClearAllAsync()
    {
        string script =
            Preamble +
            "Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.Name -like 'valve:*' -or $_.Name -like 'valve-pri:*' } | " +
            "Remove-NetQosPolicy -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue; 'ok'";

        PsOutcome outcome = await Task.Run(() => RunPowerShell(script)).ConfigureAwait(false);
        return new ShaperResult(Succeeded(outcome), "cleared all valve policies");
    }

    public async Task<IReadOnlyDictionary<string, int>> GetActiveLimitsAsync()
    {
        string script =
            Preamble +
            "Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction Stop | " +
            "Where-Object { $_.Name -like " + PsQuote(LimitPrefix + "*") + " } | " +
            "Select-Object Name, @{n='Value';e={$_.ThrottleRateAction}} | " +
            "ConvertTo-Json -Compress";

        PsOutcome outcome = await Task.Run(() => RunPowerShell(script)).ConfigureAwait(false);

        Dictionary<string, int> limits = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, double value) in ReadPolicies(outcome.StdOut))
        {
            string key = KeyFromPolicyName(name, LimitPrefix);
            if (key.Length == 0 || value <= 0)
            {
                continue;
            }

            // ThrottleRateAction is in bits per second; the UI works in kbit/s.
            int kbps = (int)Math.Clamp(Math.Round(value / 1000d), 0d, int.MaxValue);
            if (kbps > 0)
            {
                limits[key] = kbps;
            }
        }

        return limits;
    }

    public async Task<IReadOnlyDictionary<string, TrafficPriority>> GetActivePrioritiesAsync()
    {
        string script =
            Preamble +
            "Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction Stop | " +
            "Where-Object { $_.Name -like " + PsQuote(PriorityPrefix + "*") + " } | " +
            "Select-Object Name, @{n='Value';e={$_.DSCPAction}} | " +
            "ConvertTo-Json -Compress";

        PsOutcome outcome = await Task.Run(() => RunPowerShell(script)).ConfigureAwait(false);

        Dictionary<string, TrafficPriority> priorities = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, double value) in ReadPolicies(outcome.StdOut))
        {
            string key = KeyFromPolicyName(name, PriorityPrefix);
            if (key.Length == 0)
            {
                continue;
            }

            priorities[key] = PriorityFromDscp((int)Math.Round(value));
        }

        return priorities;
    }

    // ---------------------------------------------------------------- DSCP mapping

    /// <summary>
    /// DSCP values Valve writes for each priority:
    /// <list type="bullet">
    /// <item><description>Low    -> 0  (CS0 / default forwarding — no special treatment)</description></item>
    /// <item><description>Normal -> 10 (AF11 — assured forwarding, bulk class 1)</description></item>
    /// <item><description>High   -> 46 (EF — expedited forwarding, lowest latency)</description></item>
    /// </list>
    /// </summary>
    private static int DscpFor(TrafficPriority priority) => priority switch
    {
        TrafficPriority.Low => 0,
        TrafficPriority.High => 46,
        _ => 10,
    };

    /// <summary>
    /// Reverse of <see cref="DscpFor"/>. Only <c>valve-pri:</c> policies are read back,
    /// so anything outside our three values is treated as Normal.
    /// </summary>
    private static TrafficPriority PriorityFromDscp(int dscp) => dscp switch
    {
        0 => TrafficPriority.Low,
        46 => TrafficPriority.High,
        _ => TrafficPriority.Normal,
    };

    // ---------------------------------------------------------------- naming helpers

    /// <summary>Lowercase file name, the key the UI joins policy rows on.</summary>
    private static string FileNameOf(string exePath)
    {
        string name = Path.GetFileName(exePath.Trim());
        return name.Length == 0 ? exePath.Trim().ToLowerInvariant() : name.ToLowerInvariant();
    }

    /// <summary>Strips <c>valve:</c> / <c>valve-pri:</c> and normalises to a lowercase file name.</summary>
    private static string KeyFromPolicyName(string policyName, string prefix)
    {
        if (policyName.Length <= prefix.Length ||
            !policyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string tail = policyName[prefix.Length..];
        string file = Path.GetFileName(tail);
        if (file.Length == 0)
        {
            file = tail;
        }

        return file.ToLowerInvariant();
    }

    // ---------------------------------------------------------------- availability

    private void Probe()
    {
        lock (_gate)
        {
            if (_probed)
            {
                return;
            }

            _probed = true;

            try
            {
                // Fast-path: Check for NetQos module on disk directly (0.05 ms vs 2000 ms PowerShell process launch)
                string modulePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\Modules\NetQos\NetQos.psd1");
                if (File.Exists(modulePath))
                {
                    _available = true;
                    _reason = null;
                    return;
                }

                PsOutcome outcome = RunPowerShell(
                    Preamble + "Get-Command New-NetQosPolicy -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name");

                _available = outcome.ExitCode == 0 && outcome.StdOut.Trim().Length > 0;

                if (_available)
                {
                    _reason = null;
                }
                else if (outcome.ExitCode == 0)
                {
                    _reason = "New-NetQosPolicy was not found. Policy-based QoS needs Windows 10 or later.";
                }
                else
                {
                    _reason = "Could not query the NetQos cmdlets. " + DescribeFailure(outcome);
                }
            }
            catch (Exception ex)
            {
                _available = false;
                _reason = "Could not run PowerShell: " + ex.Message;
            }
        }
    }

    // ---------------------------------------------------------------- process plumbing

    /// <summary>
    /// Builds a single-quoted PowerShell string literal. Doubling embedded quotes is
    /// the only escape PowerShell needs, and it covers paths with spaces or apostrophes.
    /// </summary>
    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Runs a script with <c>powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass</c>.
    /// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/> so the runtime does
    /// the command-line quoting. Never throws; failures come back in the outcome.
    /// </summary>
    private static PsOutcome RunPowerShell(string script)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        try
        {
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                return new PsOutcome(-1, string.Empty, "PowerShell could not be started.", false);
            }

            // Read both pipes concurrently: a full stderr buffer would otherwise block
            // the child while we sit in WaitForExit.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            bool exited = process.WaitForExit(TimeoutMs);
            bool timedOut = false;

            if (!exited)
            {
                timedOut = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone, or the tree could not be killed. Nothing to do.
                }

                try
                {
                    process.WaitForExit(2_000);
                }
                catch
                {
                    // Ignore; the reads below are bounded anyway.
                }
            }

            int exitCode = exited ? process.ExitCode : -1;
            return new PsOutcome(exitCode, Drain(stdout), Drain(stderr), timedOut);
        }
        catch (Exception ex)
        {
            return new PsOutcome(-1, string.Empty, ex.Message, false);
        }
    }

    /// <summary>Collects a redirected stream without risking an unbounded wait.</summary>
    private static string Drain(Task<string> task)
    {
        try
        {
            return task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool Succeeded(PsOutcome outcome) => outcome.ExitCode == 0 && !outcome.TimedOut;

    private static string DescribeFailure(PsOutcome outcome)
    {
        if (outcome.TimedOut)
        {
            return "PowerShell did not finish within 25 seconds.";
        }

        string detail = FirstLine(outcome.StdErr);
        if (detail.Length == 0)
        {
            detail = FirstLine(outcome.StdOut);
        }

        if (detail.Length == 0)
        {
            detail = $"PowerShell exited with code {outcome.ExitCode.ToString(CultureInfo.InvariantCulture)}.";
        }

        return detail;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    // ---------------------------------------------------------------- JSON parsing

    /// <summary>
    /// Pulls <c>Name</c> plus the numeric <c>Value</c> out of the compressed JSON that
    /// <c>ConvertTo-Json</c> produced. PowerShell emits a bare object for a single
    /// result and an array for several, so both shapes are accepted.
    /// </summary>
    private static List<(string Name, double Value)> ReadPolicies(string stdout)
    {
        List<(string Name, double Value)> results = new();

        string json = (stdout ?? string.Empty).Trim();
        if (json.Length == 0)
        {
            return results;
        }

        // Skip anything PowerShell printed before the payload (warnings, etc).
        int start = json.IndexOfAny(JsonStarts);
        if (start < 0)
        {
            return results;
        }

        json = json[start..];

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray())
                {
                    Collect(item, results);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                Collect(root, results);
            }
        }
        catch (JsonException)
        {
            // Not JSON after all; report no policies rather than failing the caller.
        }

        return results;
    }

    private static void Collect(JsonElement item, List<(string Name, double Value)> sink)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("Name", out JsonElement nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string? name = nameElement.GetString();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        if (!TryReadNumber(item, "Value", out double value))
        {
            return;
        }

        sink.Add((name, value));
    }

    /// <summary>
    /// Reads a numeric property. Handles a plain number, a numeric string, and the
    /// nested CIM wrapper (<c>{"Value":800000}</c>) that some NetQos builds produce.
    /// </summary>
    private static bool TryReadNumber(JsonElement parent, string property, out double value)
    {
        value = 0;

        if (!parent.TryGetProperty(property, out JsonElement element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDouble(out value);

            case JsonValueKind.String:
                return double.TryParse(
                    element.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);

            case JsonValueKind.Object:
                foreach (JsonProperty nested in element.EnumerateObject())
                {
                    if (nested.Value.ValueKind == JsonValueKind.Number &&
                        nested.Value.TryGetDouble(out value))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private readonly record struct PsOutcome(int ExitCode, string StdOut, string StdErr, bool TimedOut);
}
