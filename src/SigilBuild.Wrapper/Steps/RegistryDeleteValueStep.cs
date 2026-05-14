namespace SigilBuild.Wrapper.Steps;

using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// MUST-tier <c>registry_delete_value</c> step. Snapshots the prior value
/// into the rollback journal before deletion. If the value is already
/// absent the step still records an "absent" rollback marker — which is a
/// no-op on undo — and succeeds silently.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryDeleteValueStep : IStep
{
    private readonly InstallStep.RegistryDeleteValue _spec;

    public RegistryDeleteValueStep(InstallStep.RegistryDeleteValue spec)
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

        var resolvedKey = ctx.Resolve(_spec.Key);
        var resolvedName = ctx.Resolve(_spec.Name);
        var hive = RegistryHelper.ParseHive(_spec.Hive);
        var view = RegistryHelper.ParseView(_spec.View);
        var snap = RegistryHelper.Snapshot(hive, resolvedKey, resolvedName, view);

        // Record rollback BEFORE mutation.
        journal.Append(new RollbackRecord.RestoreRegistryValue(
            Hive: _spec.Hive,
            Key: resolvedKey,
            Name: resolvedName,
            View: _spec.View,
            PriorTypeStr: snap.PreviouslyAbsent ? null : RegistryHelper.ValueKindToString(snap.Kind),
            PriorValue: snap.Value,
            PreviouslyAbsent: snap.PreviouslyAbsent));

        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var sub = baseKey.OpenSubKey(resolvedKey, writable: true);
        if (sub is not null)
        {
            sub.DeleteValue(resolvedName, throwOnMissingValue: false);
        }
        return Task.FromResult(StepResult.Ok());
    }
}
