using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Valve.Core;

/// <summary>
/// One application-level block/allow decision. <see cref="Id"/> doubles as the
/// WFP filter key, so it must be a 32-character hex GUID.
/// </summary>
public sealed class FirewallRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AppPath { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Block { get; set; }
    public bool Enabled { get; set; } = true;
}

public interface IFirewall
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    IReadOnlyList<FirewallRule> ActiveRules { get; }

    Task<(bool Ok, string Message)> SyncAsync(IReadOnlyList<FirewallRule> desired);
    Task<(bool Ok, string Message)> ClearAllAsync();
    Task<(bool Ok, string Message)> SetEmergencyBlockAsync(bool blockAll);
}

/// <summary>
/// Per-application block/allow implemented with the Windows Filtering Platform
/// from user mode only (fwpuclnt.dll). There is no kernel driver: the filters
/// live in this process's WFP engine session, so they disappear when Valve exits
/// or crashes — which is the behaviour we want for a live bandwidth controller.
/// </summary>
public sealed class WfpFirewall : IFirewall, IDisposable
{
    // ---------------------------------------------------------------------
    // Ownership. Every object Valve adds is tagged with ProviderKey so the
    // reconciliation pass can enumerate *only* Valve's filters and never touch
    // Windows' own rules. These two GUIDs are Valve's identity in the filter
    // table: regenerating them would orphan every filter installed earlier.
    // ---------------------------------------------------------------------
    private static readonly Guid ProviderKey = new("8f2a1c4e-6b3d-4a19-9f52-1d7c0b6e5a31");
    private static readonly Guid SubLayerKey = new("c6d1e0b4-2f57-4c8a-8b3e-5a9d4f7c1e26");

    private static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971"); // FWPM_CONDITION_ALE_APP_ID

    // One filter per layer per rule: outbound connect + inbound accept, for both
    // address families. The app-id blob is family-agnostic, so the same blob is
    // used for all four.
    private static readonly Guid[] Layers =
    {
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82"), // FWPM_LAYER_ALE_AUTH_CONNECT_V4
        new("e1cd9fe7-f4b5-4273-96c0-592e487b8650"), // FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"), // FWPM_LAYER_ALE_AUTH_CONNECT_V6       // VERIFY
        new("a3b42c97-9f04-4672-b87e-cee9c483257f"), // FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6   // VERIFY
    };

    private static readonly string[] LayerNames =
    {
        "ALE_AUTH_CONNECT_V4",
        "ALE_AUTH_RECV_ACCEPT_V4",
        "ALE_AUTH_CONNECT_V6",
        "ALE_AUTH_RECV_ACCEPT_V6",
    };

    // Sublayer weight is the maximum 16-bit value so Valve's filters are
    // evaluated ahead of the built-in sublayers. See the permit caveat below.
    private const ushort SubLayerWeight = 0xFFFF;
    private const byte FilterWeight = 0x0F;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private IntPtr _engine;
    private List<FirewallRule> _active = new();
    private bool _sublayerReady;
    private bool _providerReady;
    private readonly List<ulong> _emergencyFilterIds = new();
    private bool _disposed;

    public WfpFirewall()
    {
        // Never throw: a missing fwpuclnt.dll or a non-elevated process must
        // still leave a usable object that reports why it is inert.
        try
        {
            uint status = Wfp.EngineOpen(null, Wfp.RPC_C_AUTHN_WINNT, IntPtr.Zero, IntPtr.Zero, out IntPtr engine);
            if (status != 0)
            {
                _engine = IntPtr.Zero;
                UnavailableReason = DescribeOpenFailure(status);
                return;
            }

            _engine = engine;
        }
        catch (DllNotFoundException)
        {
            _engine = IntPtr.Zero;
            UnavailableReason = "fwpuclnt.dll is not available on this system.";
        }
        catch (EntryPointNotFoundException)
        {
            _engine = IntPtr.Zero;
            UnavailableReason = "fwpuclnt.dll does not export the required WFP entry points.";
        }
        catch (Exception ex)
        {
            _engine = IntPtr.Zero;
            UnavailableReason = ex.Message;
        }
    }

    public bool IsAvailable => _engine != IntPtr.Zero && !_disposed;

    public string? UnavailableReason { get; private set; }

    public IReadOnlyList<FirewallRule> ActiveRules
    {
        get
        {
            lock (_stateLock)
            {
                return _active.ToArray();
            }
        }
    }

    // ---------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------

    public async Task<(bool Ok, string Message)> SyncAsync(IReadOnlyList<FirewallRule> desired)
    {
        List<FirewallRule> snapshot;
        try
        {
            snapshot = Normalize(desired);
        }
        catch (Exception ex)
        {
            return (false, $"Could not read the rule set: {ex.Message}");
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SyncCore(snapshot)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Last line of defence: nothing may escape into the UI layer.
            return (false, $"Firewall sync failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool Ok, string Message)> ClearAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(ClearCore).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, $"Firewall clear failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------------
    // Reconciliation
    // ---------------------------------------------------------------------

    private (bool Ok, string Message) SyncCore(List<FirewallRule> desired)
    {
        if (_disposed)
        {
            return (false, "The firewall engine has been disposed.");
        }

        if (_engine == IntPtr.Zero)
        {
            return (false, UnavailableReason ?? "The Windows Filtering Platform is not available.");
        }

        (bool subOk, string subMessage) = EnsureSubLayer();
        if (!subOk)
        {
            return (false, subMessage);
        }

        var errors = new List<string>();
        var pending = new List<PendingRule>();

        // Phase 1: resolve every rule to an app-id blob. Blobs stay alive until
        // the whole sync is finished because the filter conditions point at them.
        try
        {
            var usedIds = new HashSet<Guid>();
            foreach (FirewallRule rule in desired)
            {
                Guid ruleKey = Guid.Parse(rule.Id);
                if (!usedIds.Add(ruleKey))
                {
                    // Two rules sharing an Id would fight over the same filter key.
                    errors.Add($"{Describe(rule)}: duplicate rule id ignored.");
                    continue;
                }

                uint status = Wfp.GetAppId(rule.AppPath, out IntPtr appId);
                if (status != 0 || appId == IntPtr.Zero)
                {
                    errors.Add($"{Describe(rule)}: could not resolve the app id (Win32 error {status}).");
                    continue;
                }

                pending.Add(new PendingRule(rule, ruleKey, appId, rule.Block ? Wfp.FWP_ACTION_BLOCK : Wfp.FWP_ACTION_PERMIT));
            }

            // Identity of a filter Valve would install: key + layer + action.
            // The action is part of the identity so flipping block->permit
            // rewrites the filter instead of silently keeping the old one.
            var wanted = new HashSet<(Guid Key, Guid Layer, uint Action)>();
            foreach (PendingRule p in pending)
            {
                for (int i = 0; i < Layers.Length; i++)
                {
                    wanted.Add((FilterKey(p.RuleKey, i), Layers[i], p.Action));
                }
            }

            // Phase 2: drop Valve-owned filters that are stale, duplicated or
            // no longer wanted. Foreign filters are never enumerated, let alone
            // deleted, because the enumeration is scoped to our provider key.
            List<OwnedFilter> existing = EnumerateOwned(out uint enumStatus);
            if (enumStatus != 0)
            {
                return (false, $"Could not enumerate Valve's filters (Win32 error {enumStatus}).");
            }

            var kept = new HashSet<(Guid Key, Guid Layer, uint Action)>();
            int removed = 0;
            foreach (OwnedFilter filter in existing)
            {
                (Guid, Guid, uint) identity = (filter.FilterKey, filter.LayerKey, filter.Action);
                bool keep = wanted.Contains(identity) && kept.Add(identity);
                if (keep)
                {
                    continue;
                }

                uint status = Wfp.FilterDeleteById(_engine, filter.FilterId);
                if (status == 0 || status == Wfp.ERROR_FWP_FILTER_NOT_FOUND)
                {
                    removed++;
                }
                else
                {
                    errors.Add($"could not remove stale filter {filter.FilterKey} (Win32 error {status}).");
                }
            }

            // Phase 3: add what is missing.
            var installed = new List<FirewallRule>();
            foreach (PendingRule p in pending)
            {
                bool ruleOk = true;
                for (int i = 0; i < Layers.Length; i++)
                {
                    (Guid Key, Guid Layer, uint Action) identity = (FilterKey(p.RuleKey, i), Layers[i], p.Action);
                    if (kept.Contains(identity))
                    {
                        continue;
                    }

                    uint status = AddFilter(identity.Key, identity.Layer, identity.Action, p.AppId, Describe(p.Rule));
                    if (status != 0)
                    {
                        ruleOk = false;
                        errors.Add($"{Describe(p.Rule)}: {LayerNames[i]} add failed (Win32 error {status}).");
                    }
                }

                if (ruleOk)
                {
                    installed.Add(p.Rule);
                }
            }

            lock (_stateLock)
            {
                _active = installed;
            }

            if (errors.Count > 0)
            {
                return (false, string.Join(" ", errors.Take(4)) + (errors.Count > 4 ? $" (+{errors.Count - 4} more)" : string.Empty));
            }

            return (true, installed.Count == 0
                ? $"Firewall synced: no rules enforced, {removed} filter(s) removed."
                : $"Firewall synced: {installed.Count} app(s) enforced, {removed} stale filter(s) removed.");
        }
        finally
        {
            foreach (PendingRule p in pending)
            {
                IntPtr blob = p.AppId;
                if (blob != IntPtr.Zero)
                {
                    Wfp.FreeMemory(ref blob);
                }
            }
        }
    }

    private (bool Ok, string Message) ClearCore()
    {
        if (_disposed)
        {
            return (false, "The firewall engine has been disposed.");
        }

        if (_engine == IntPtr.Zero)
        {
            return (false, UnavailableReason ?? "The Windows Filtering Platform is not available.");
        }

        try
        {
            List<OwnedFilter> existing = EnumerateOwned(out uint enumStatus);
            if (enumStatus != 0)
            {
                return (false, $"Could not enumerate Valve's filters (Win32 error {enumStatus}).");
            }

            var errors = new List<string>();
            int removed = 0;
            foreach (OwnedFilter filter in existing)
            {
                uint status = Wfp.FilterDeleteById(_engine, filter.FilterId);
                if (status == 0 || status == Wfp.ERROR_FWP_FILTER_NOT_FOUND)
                {
                    removed++;
                }
                else
                {
                    errors.Add($"Win32 error {status} for filter {filter.FilterKey}.");
                }
            }

            lock (_stateLock)
            {
                _active = new List<FirewallRule>();
            }

            if (errors.Count > 0)
            {
                return (false, string.Join(" ", errors.Take(4)));
            }

            return (true, $"Firewall cleared: {removed} filter(s) removed.");
        }
        catch (Exception ex)
        {
            return (false, $"Firewall clear failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------
    // Filter construction
    // ---------------------------------------------------------------------

    /// <summary>
    /// Installs one filter. <paramref name="action"/> is FWP_ACTION_BLOCK or
    /// FWP_ACTION_PERMIT.
    /// </summary>
    /// <remarks>
    /// Permit caveat: Valve's filters live in its own sublayer, weighted 0xFFFF
    /// so it is evaluated before the built-in sublayers. A PERMIT filter is a
    /// terminating action, so it reliably overrides a Valve BLOCK and stops
    /// evaluation for that connection. The flip side is that it also overrides
    /// Windows Defender Firewall's own block rules for that executable. That is
    /// the honest meaning of "explicitly allow" here; it is not a silent no-op.
    /// </remarks>
    private uint AddFilter(Guid filterKey, Guid layerKey, uint action, IntPtr appId, string label)
    {
        IntPtr namePtr = IntPtr.Zero;
        IntPtr conditionPtr = IntPtr.Zero;
        IntPtr filterPtr = IntPtr.Zero;
        IntPtr providerPtr = IntPtr.Zero;

        try
        {
            namePtr = Marshal.StringToHGlobalUni(label);

            conditionPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Wfp.FILTER_CONDITION0>());
            var condition = new Wfp.FILTER_CONDITION0
            {
                fieldKey = ConditionAppId,
                matchType = Wfp.FWP_MATCH_EQUAL,
                conditionValue = new Wfp.VALUE0 { type = Wfp.FWP_BYTE_BLOB_TYPE, ptr = appId },
            };
            Marshal.StructureToPtr(condition, conditionPtr, false);

            // Only providerKey is a pointer in FWPM_FILTER0; layerKey and
            // subLayerKey are inline GUIDs.
            providerPtr = _providerReady ? AllocGuid(ProviderKey) : IntPtr.Zero;

            var filter = new Wfp.FILTER0
            {
                filterKey = filterKey,
                displayData = new Wfp.DISPLAY_DATA0 { name = namePtr, description = IntPtr.Zero },
                flags = 0, // deliberately not FWPM_FILTER_FLAG_PERSISTENT
                providerKey = providerPtr,
                providerData = default,
                layerKey = layerKey,
                subLayerKey = SubLayerKey,
                weight = new Wfp.VALUE0 { type = Wfp.FWP_UINT8, value = FilterWeight },
                numFilterConditions = 1,
                filterCondition = conditionPtr,
                action = new Wfp.ACTION0 { type = action, filterType = Guid.Empty },
                context = default,
                reserved = IntPtr.Zero,
                filterId = 0,
                effectiveWeight = default,
            };

            filterPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Wfp.FILTER0>());
            Marshal.StructureToPtr(filter, filterPtr, false);

            return Wfp.FilterAdd(_engine, filterPtr, IntPtr.Zero, out _);
        }
        finally
        {
            Free(namePtr);
            Free(conditionPtr);
            Free(providerPtr);
            Free(filterPtr);
        }
    }

    private (bool Ok, string Message) EnsureProvider()
    {
        if (_providerReady)
        {
            return (true, string.Empty);
        }

        IntPtr namePtr = IntPtr.Zero;
        IntPtr descPtr = IntPtr.Zero;
        IntPtr providerPtr = IntPtr.Zero;

        try
        {
            namePtr = Marshal.StringToHGlobalUni("Valve");
            descPtr = Marshal.StringToHGlobalUni("Valve Network Control Provider");

            var provider = new Wfp.PROVIDER0
            {
                providerKey = ProviderKey,
                displayData = new Wfp.DISPLAY_DATA0 { name = namePtr, description = descPtr },
                flags = 0,
            };

            providerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Wfp.PROVIDER0>());
            Marshal.StructureToPtr(provider, providerPtr, false);

            uint status = Wfp.ProviderAdd(_engine, providerPtr, IntPtr.Zero);
            if (status == 0 || status == Wfp.ERROR_ALREADY_EXISTS || status == Wfp.ERROR_FWP_ALREADY_EXISTS)
            {
                _providerReady = true;
                return (true, string.Empty);
            }

            return (false, $"Could not register Valve WFP provider (Win32 error {status}).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            Free(namePtr);
            Free(descPtr);
            Free(providerPtr);
        }
    }

    private (bool Ok, string Message) EnsureSubLayer()
    {
        if (_sublayerReady)
        {
            return (true, string.Empty);
        }

        EnsureProvider();

        IntPtr namePtr = IntPtr.Zero;
        IntPtr descPtr = IntPtr.Zero;
        IntPtr subLayerPtr = IntPtr.Zero;
        IntPtr providerPtr = IntPtr.Zero;

        try
        {
            namePtr = Marshal.StringToHGlobalUni("Valve");
            descPtr = Marshal.StringToHGlobalUni("Application block/allow filters installed by Valve.");
            providerPtr = _providerReady ? AllocGuid(ProviderKey) : IntPtr.Zero;

            var subLayer = new Wfp.SUBLAYER0
            {
                subLayerKey = SubLayerKey,
                displayData = new Wfp.DISPLAY_DATA0 { name = namePtr, description = descPtr },
                flags = 0,
                providerKey = providerPtr,
                providerData = default,
                weight = SubLayerWeight,
            };

            subLayerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Wfp.SUBLAYER0>());
            Marshal.StructureToPtr(subLayer, subLayerPtr, false);

            uint status = Wfp.SubLayerAdd(_engine, subLayerPtr, IntPtr.Zero);
            // A previous Valve instance (or an earlier engine session) may already
            // have created it. WFP reports that as either a Win32 or an FWP error.
            if (status != 0 && status != Wfp.ERROR_ALREADY_EXISTS && status != Wfp.ERROR_FWP_ALREADY_EXISTS)
            {
                return (false, $"Could not create the Valve WFP sublayer (Win32 error {status}).");
            }

            _sublayerReady = true;
            return (true, string.Empty);
        }
        finally
        {
            Free(namePtr);
            Free(descPtr);
            Free(providerPtr);
            Free(subLayerPtr);
        }
    }

    public async Task<(bool Ok, string Message)> SetEmergencyBlockAsync(bool blockAll)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                if (_disposed || _engine == IntPtr.Zero)
                {
                    return (false, "Firewall engine is not available.");
                }

                (bool subOk, string subMsg) = EnsureSubLayer();
                if (!subOk)
                {
                    return (false, subMsg);
                }

                lock (_emergencyFilterIds)
                {
                    foreach (ulong id in _emergencyFilterIds)
                    {
                        try { Wfp.FilterDeleteById(_engine, id); } catch { }
                    }
                    _emergencyFilterIds.Clear();
                }

                if (!blockAll)
                {
                    return (true, "Emergency block deactivated.");
                }

                int added = 0;
                for (int i = 0; i < Layers.Length; i++)
                {
                    IntPtr namePtr = IntPtr.Zero;
                    IntPtr filterPtr = IntPtr.Zero;
                    IntPtr providerPtr = IntPtr.Zero;
                    try
                    {
                        namePtr = Marshal.StringToHGlobalUni("Valve Emergency Block");
                        providerPtr = _providerReady ? AllocGuid(ProviderKey) : IntPtr.Zero;

                        var filter = new Wfp.FILTER0
                        {
                            filterKey = Guid.NewGuid(),
                            displayData = new Wfp.DISPLAY_DATA0 { name = namePtr, description = IntPtr.Zero },
                            flags = 0,
                            providerKey = providerPtr,
                            providerData = default,
                            layerKey = Layers[i],
                            subLayerKey = SubLayerKey,
                            weight = new Wfp.VALUE0 { type = Wfp.FWP_UINT8, value = 0xFF },
                            numFilterConditions = 0,
                            filterCondition = IntPtr.Zero,
                            action = new Wfp.ACTION0 { type = Wfp.FWP_ACTION_BLOCK, filterType = Guid.Empty },
                            context = default,
                            reserved = IntPtr.Zero,
                            filterId = 0,
                            effectiveWeight = default,
                        };

                        filterPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Wfp.FILTER0>());
                        Marshal.StructureToPtr(filter, filterPtr, false);

                        uint status = Wfp.FilterAdd(_engine, filterPtr, IntPtr.Zero, out ulong id);
                        if (status == 0)
                        {
                            lock (_emergencyFilterIds)
                            {
                                _emergencyFilterIds.Add(id);
                            }
                            added++;
                        }
                    }
                    finally
                    {
                        Free(namePtr);
                        Free(providerPtr);
                        Free(filterPtr);
                    }
                }

                return (added > 0, $"Emergency block activated ({added} layers enforced).");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, $"Emergency block error: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<OwnedFilter> EnumerateOwned(out uint error)
    {
        var found = new List<OwnedFilter>();
        error = 0;

        Guid provider = ProviderKey;
        uint status = Wfp.FilterEnumByProvider(_engine, ref provider, out IntPtr enumHandle);
        if (status != 0)
        {
            error = status;
            return found;
        }

        try
        {
            while (true)
            {
                IntPtr entries = IntPtr.Zero;
                status = Wfp.FilterEnum(_engine, enumHandle, 64, out entries, out uint returned);
                if (status != 0)
                {
                    error = status;
                    break;
                }

                if (returned == 0 || entries == IntPtr.Zero)
                {
                    Free(ref entries);
                    break;
                }

                try
                {
                    for (uint i = 0; i < returned; i++)
                    {
                        IntPtr item = Marshal.ReadIntPtr(entries, (int)i * IntPtr.Size);
                        if (item == IntPtr.Zero)
                        {
                            continue;
                        }

                        var filter = Marshal.PtrToStructure<Wfp.FILTER0>(item);
                        found.Add(new OwnedFilter(filter.filterKey, filter.layerKey, filter.action.type, filter.filterId));
                    }
                }
                finally
                {
                    Free(ref entries);
                }
            }
        }
        finally
        {
            Wfp.FilterDestroyEnumHandle(_engine, enumHandle);
        }

        return found;
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Filter key for one (rule, layer) pair. Layer 0 uses the rule id verbatim;
    /// the other three layers get a deterministic variant, because a filter key
    /// has to identify a single filter (FwpmFilterGetByKey0 / DeleteByKey0 assume
    /// that) and one rule needs four filters.
    /// </summary>
    private static Guid FilterKey(Guid ruleKey, int layerIndex)
    {
        if (layerIndex == 0)
        {
            return ruleKey;
        }

        Span<byte> bytes = stackalloc byte[16];
        ruleKey.TryWriteBytes(bytes);
        bytes[15] ^= (byte)(0xA0 | layerIndex);
        return new Guid(bytes);
    }

    private static List<FirewallRule> Normalize(IReadOnlyList<FirewallRule>? desired)
    {
        var byPath = new Dictionary<string, FirewallRule>(StringComparer.OrdinalIgnoreCase);
        if (desired == null)
        {
            return new List<FirewallRule>();
        }

        foreach (FirewallRule rule in desired)
        {
            if (rule == null || !rule.Enabled || string.IsNullOrWhiteSpace(rule.AppPath))
            {
                continue;
            }

            if (!Guid.TryParse(rule.Id, out _))
            {
                continue;
            }

            string path;
            try
            {
                path = Path.GetFullPath(rule.AppPath.Trim());
            }
            catch (Exception)
            {
                continue;
            }

            if (byPath.TryGetValue(path, out FirewallRule? existing) && existing.Block && !rule.Block)
            {
                // Block always wins over permit for the same executable, so the
                // result does not depend on list ordering.
                continue;
            }

            // Copy rather than mutate the caller's objects: the caller's rules
            // are settings objects and must not change behind its back.
            byPath[path] = new FirewallRule
            {
                Id = rule.Id,
                AppPath = path,
                Label = rule.Label,
                Block = rule.Block,
                Enabled = true,
            };
        }

        return byPath.Values.ToList();
    }

    private static string Describe(FirewallRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Label))
        {
            return rule.Label;
        }

        try
        {
            return Path.GetFileName(rule.AppPath) is { Length: > 0 } name ? name : rule.AppPath;
        }
        catch (Exception)
        {
            return rule.AppPath;
        }
    }

    private static string DescribeOpenFailure(uint status)
    {
        if (status == Wfp.ERROR_ACCESS_DENIED)
        {
            return "administrator rights required (Win32 error 5: access denied). Restart Valve elevated to manage firewall rules.";
        }

        string detail;
        try
        {
            detail = new Win32Exception((int)status).Message;
        }
        catch (Exception)
        {
            detail = "unknown error";
        }

        return $"the WFP engine could not be opened (Win32 error {status}: {detail}).";
    }

    private static IntPtr AllocGuid(Guid value)
    {
        IntPtr pointer = Marshal.AllocHGlobal(16);
        Marshal.StructureToPtr(value, pointer, false);
        return pointer;
    }

    private static void Free(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void Free(ref IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Wfp.FreeMemory(ref pointer);
            pointer = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_engine != IntPtr.Zero)
        {
            try
            {
                // Non-persistent filters are removed when the last engine handle
                // for this session closes, so nothing is left behind.
                Wfp.EngineClose(_engine);
            }
            catch (Exception)
            {
                // Shutting down must not throw.
            }

            _engine = IntPtr.Zero;
        }

        lock (_stateLock)
        {
            _active = new List<FirewallRule>();
        }
    }

    private readonly record struct PendingRule(FirewallRule Rule, Guid RuleKey, IntPtr AppId, uint Action);

    private readonly record struct OwnedFilter(Guid FilterKey, Guid LayerKey, uint Action, ulong FilterId);

    // =====================================================================
    // Raw WFP surface. Kept private and nested so the file is self-contained
    // and cannot collide with anything else in Valve.Core.
    // =====================================================================
    private static class Wfp
    {
        public const uint RPC_C_AUTHN_WINNT = 10;

        public const uint ERROR_ACCESS_DENIED = 5;
        public const uint ERROR_ALREADY_EXISTS = 183;

        // FWP_E_* errors use facility 0x0F, not the Win32 facility.
        public const uint ERROR_FWP_FILTER_NOT_FOUND = 0x80320003;
        public const uint ERROR_FWP_ALREADY_EXISTS = 0x80320009;

        // FWP_DATA_TYPE
        public const uint FWP_UINT8 = 1;
        public const uint FWP_BYTE_BLOB_TYPE = 12;

        // FWP_MATCH_TYPE
        public const uint FWP_MATCH_EQUAL = 0;

        // FWP_ACTION_TYPE (both include FWP_ACTION_FLAG_TERMINATING)
        public const uint FWP_ACTION_BLOCK = 0x00001001;
        public const uint FWP_ACTION_PERMIT = 0x00001002;

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAY_DATA0
        {
            public IntPtr name;
            public IntPtr description;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROVIDER0
        {
            public Guid providerKey;
            public DISPLAY_DATA0 displayData;
            public uint flags;
            public BYTE_BLOB providerData;
            public IntPtr serviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BYTE_BLOB
        {
            public uint size;
            public IntPtr data;
        }

        /// <summary>
        /// FWP_VALUE0. The native union is pointer-sized on x64 (4 bytes of type,
        /// 4 bytes of padding, then the payload), so this is pinned to 16 bytes.
        /// Small integers live inline in the payload; the app-id condition stores
        /// a pointer to an FWP_BYTE_BLOB there. GUID members are stored as
        /// pointers too, hence no Guid field.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        public struct VALUE0
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public ulong value;
            [FieldOffset(8)] public IntPtr ptr;
        }

        /// <summary>
        /// FWPM_FILTER_CONDITION0, 40 bytes on x64: fieldKey 0, matchType 16,
        /// conditionValue 24 (pointer alignment after the enum). Pinned
        /// explicitly because the payload's alignment is the one thing the
        /// default packer could plausibly get wrong.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        public struct FILTER_CONDITION0
        {
            [FieldOffset(0)] public Guid fieldKey;
            [FieldOffset(16)] public uint matchType;
            [FieldOffset(24)] public VALUE0 conditionValue; // FWP_CONDITION_VALUE0, same layout as FWP_VALUE0
        }

        /// <summary>
        /// FWPM_ACTION0, 20 bytes: type at 0, then the { filterType; calloutKey; }
        /// union at 4 (GUID has 4-byte alignment, so there is no padding).
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 20)]
        public struct ACTION0
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(4)] public Guid filterType;
        }

        /// <summary>
        /// The { UINT64 rawContext; GUID providerContextKey; } union of FWPM_FILTER0.
        /// Valve always leaves it zero; two UINT64s reproduce its 16-byte size.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CONTEXT0
        {
            public ulong rawContext;
            public ulong padding;
        }

        /// <summary>
        /// FWPM_FILTER0 for Windows 10/11. Field order and the resulting x64
        /// offsets: filterKey 0, displayData 16, flags 32, providerKey 40,
        /// providerData 48, layerKey 64, subLayerKey 80, weight 96,
        /// numFilterConditions 112, filterCondition 120, action 128,
        /// context 152, reserved 168, filterId 176, effectiveWeight 184,
        /// sizeof 200.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct FILTER0
        {
            public Guid filterKey;
            public DISPLAY_DATA0 displayData;
            public uint flags;
            public IntPtr providerKey;
            public BYTE_BLOB providerData;
            public Guid layerKey;
            public Guid subLayerKey;
            public VALUE0 weight;
            public uint numFilterConditions;
            public IntPtr filterCondition;
            public ACTION0 action;
            public CONTEXT0 context;
            public IntPtr reserved;
            public ulong filterId;
            public VALUE0 effectiveWeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SUBLAYER0
        {
            public Guid subLayerKey;
            public DISPLAY_DATA0 displayData;
            public ushort flags;
            public IntPtr providerKey;
            public BYTE_BLOB providerData;
            public ushort weight;
        }

        [DllImport("fwpuclnt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint FwpmEngineOpen0(
            string? serverName,
            uint authnService,
            IntPtr authIdentity,
            IntPtr session,
            out IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", SetLastError = true)]
        public static extern uint FwpmEngineClose0(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

        [DllImport("fwpuclnt.dll", SetLastError = true)]
        public static extern uint FwpmFilterAdd0(IntPtr engineHandle, IntPtr filter, IntPtr sd, out ulong id);

        [DllImport("fwpuclnt.dll", SetLastError = true)]
        public static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

        [DllImport("fwpuclnt.dll", SetLastError = true)]
        public static extern uint FwpmFilterEnumByProvider0(IntPtr engineHandle, ref Guid providerKey, out IntPtr enumHandle);

        [DllImport("fwpuclnt.dll", SetLastError = true)]
        public static extern uint FwpmFilterEnum0(
            IntPtr engineHandle,
            IntPtr enumHandle,
            uint numEntriesRequested,
            out IntPtr entries,
            out uint numEntriesReturned);

        [DllImport("fwpuclnt.dll", SetLastError = true)]
        public static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);

        [DllImport("fwpuclnt.dll", SetLastError = true)]
        public static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, IntPtr subLayer, IntPtr sd);

        [DllImport("fwpuclnt.dll", SetLastError = true)]
        public static extern uint FwpmProviderAdd0(IntPtr engineHandle, IntPtr provider, IntPtr sd);

        [DllImport("fwpuclnt.dll", SetLastError = true)]
        public static extern void FwpmFreeMemory0(ref IntPtr p);

        // Friendly wrappers keep the call sites readable.
        public static uint EngineOpen(string? serverName, uint authn, IntPtr authIdentity, IntPtr session, out IntPtr handle)
            => FwpmEngineOpen0(serverName, authn, authIdentity, session, out handle);

        public static void EngineClose(IntPtr handle) => FwpmEngineClose0(handle);

        public static uint GetAppId(string fileName, out IntPtr appId) => FwpmGetAppIdFromFileName0(fileName, out appId);

        public static uint FilterAdd(IntPtr engine, IntPtr filter, IntPtr sd, out ulong id)
            => FwpmFilterAdd0(engine, filter, sd, out id);

        public static uint FilterDeleteById(IntPtr engine, ulong id) => FwpmFilterDeleteById0(engine, id);

        public static uint FilterEnumByProvider(IntPtr engine, ref Guid providerKey, out IntPtr enumHandle)
            => FwpmFilterEnumByProvider0(engine, ref providerKey, out enumHandle);

        public static uint FilterEnum(IntPtr engine, IntPtr enumHandle, uint requested, out IntPtr entries, out uint returned)
            => FwpmFilterEnum0(engine, enumHandle, requested, out entries, out returned);

        public static uint FilterDestroyEnumHandle(IntPtr engine, IntPtr enumHandle)
            => FwpmFilterDestroyEnumHandle0(engine, enumHandle);

        public static uint SubLayerAdd(IntPtr engine, IntPtr subLayer, IntPtr sd)
            => FwpmSubLayerAdd0(engine, subLayer, sd);

        public static uint ProviderAdd(IntPtr engine, IntPtr provider, IntPtr sd)
            => FwpmProviderAdd0(engine, provider, sd);

        public static void FreeMemory(ref IntPtr p) => FwpmFreeMemory0(ref p);
    }
}
