namespace SigilBuild.Wrapper.Steps;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// MUST-tier <c>registry_write</c> step. Captures the prior value (if any)
/// into the rollback journal BEFORE mutating, then writes the typed value
/// with the manifest-supplied <c>type:</c> kind under the requested
/// hive/key/view. Coerces YAML scalars (strings, longs, byte[]) into the
/// runtime type expected by <see cref="RegistryKey.SetValue(string?, object, RegistryValueKind)"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryWriteStep : IStep
{
    private readonly InstallStep.RegistryWrite _spec;

    public RegistryWriteStep(InstallStep.RegistryWrite spec)
    {
        _spec = spec;
    }

    public Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(StepResult.Failed("registry steps require Windows"));
        }

        // Resolve ${parameters.*} / ${app.*} / ${env.*} placeholders. Before
        // this, the literal template text landed in the registry (e.g.
        // InstallDir = "${parameters.install_dir}", InstalledBy = "Sigil
        // ${app.version}") — a silent data-corruption bug because the engine
        // reported the step as successful even though the value was useless.
        var resolvedKey = ctx.Resolve(_spec.Key);
        var resolvedName = ctx.Resolve(_spec.Name);
        var resolvedValue = _spec.Value is string s ? ctx.Resolve(s) : _spec.Value;

        var hive = RegistryHelper.ParseHive(_spec.Hive);
        var view = RegistryHelper.ParseView(_spec.View);
        var kind = RegistryHelper.ParseValueKind(_spec.Type);

        var snap = RegistryHelper.Snapshot(hive, resolvedKey, resolvedName, view);

        // Record rollback BEFORE mutation so a crash mid-write still leaves
        // the journal in a state that restores correctly. The journal stores
        // the RESOLVED key/name so uninstall doesn't need ctx to re-resolve.
        journal.Append(new RollbackRecord.RestoreRegistryValue(
            Hive: _spec.Hive,
            Key: resolvedKey,
            Name: resolvedName,
            View: _spec.View,
            PriorTypeStr: snap.PreviouslyAbsent ? null : RegistryHelper.ValueKindToString(snap.Kind),
            PriorValue: snap.Value,
            PreviouslyAbsent: snap.PreviouslyAbsent));

        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var sub = baseKey.CreateSubKey(resolvedKey, writable: true);
        if (sub is null)
        {
            return Task.FromResult(StepResult.Failed(
                $"could not open or create key {_spec.Hive}\\{resolvedKey}"));
        }

        var coerced = CoerceValue(resolvedValue, kind);
        sub.SetValue(resolvedName, coerced, kind);
        return Task.FromResult(StepResult.Ok());
    }

    /// <summary>
    /// Coerce a manifest-parsed YAML scalar/sequence into the runtime type
    /// the underlying Win32 registry API expects for <paramref name="kind"/>.
    /// Numeric kinds parse via the invariant culture; <c>REG_BINARY</c>
    /// accepts a hex string fallback; <c>REG_MULTI_SZ</c> accepts a single
    /// string or any string sequence.
    /// </summary>
    private static object CoerceValue(object? raw, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString =>
            raw?.ToString() ?? string.Empty,

        RegistryValueKind.DWord => raw is null
            ? 0
            : Convert.ToInt32(raw, CultureInfo.InvariantCulture),

        RegistryValueKind.QWord => raw is null
            ? 0L
            : Convert.ToInt64(raw, CultureInfo.InvariantCulture),

        RegistryValueKind.MultiString => raw switch
        {
            string[] arr => arr,
            IEnumerable<string> seq => seq.ToArray(),
            null => Array.Empty<string>(),
            _ => new[] { raw.ToString() ?? string.Empty },
        },

        RegistryValueKind.Binary => raw switch
        {
            byte[] b => b,
            string s => Convert.FromHexString(s),
            null => Array.Empty<byte>(),
            _ => Array.Empty<byte>(),
        },

        _ => raw ?? string.Empty,
    };
}
