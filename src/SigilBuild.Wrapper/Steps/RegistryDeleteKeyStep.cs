namespace SigilBuild.Wrapper.Steps;

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// MUST-tier <c>registry_delete_key</c> step. Snapshots the immediate
/// values directly under the key (NOT a recursive subtree) into the
/// rollback journal, then deletes the key — recursively when
/// <see cref="InstallStep.RegistryDeleteKey.Recursive"/> is set.
/// </summary>
/// <remarks>
/// KNOWN GAP: when <see cref="InstallStep.RegistryDeleteKey.Recursive"/>
/// is true and the deleted subtree contained nested subkeys / values,
/// rollback only re-creates the immediate key with its top-level values.
/// Nested subtree restore is deferred — see Task 19's
/// <c>uninstall.json</c> work for a more durable serialization.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class RegistryDeleteKeyStep : IStep
{
    private readonly InstallStep.RegistryDeleteKey _spec;

    public RegistryDeleteKeyStep(InstallStep.RegistryDeleteKey spec)
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
        var hive = RegistryHelper.ParseHive(_spec.Hive);
        var view = RegistryHelper.ParseView(_spec.View);

        // Snapshot immediate-key values BEFORE mutation.
        bool previouslyAbsent;
        var values = new List<RegistryValueSnapshot>();
        using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
        using (var sub = baseKey.OpenSubKey(resolvedKey, writable: false))
        {
            if (sub is null)
            {
                previouslyAbsent = true;
            }
            else
            {
                previouslyAbsent = false;
                foreach (var name in sub.GetValueNames())
                {
                    var v = sub.GetValue(name);
                    if (v is null)
                    {
                        continue;
                    }
                    values.Add(new RegistryValueSnapshot(
                        name,
                        RegistryHelper.ValueKindToString(sub.GetValueKind(name)),
                        v));
                }
            }
        }

        journal.Append(new RollbackRecord.RestoreRegistryKey(
            Hive: _spec.Hive,
            Key: resolvedKey,
            View: _spec.View,
            ValuesAtKeyLevel: values,
            PreviouslyAbsent: previouslyAbsent));

        if (!previouslyAbsent)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            if (_spec.Recursive)
            {
                baseKey.DeleteSubKeyTree(resolvedKey, throwOnMissingSubKey: false);
            }
            else
            {
                baseKey.DeleteSubKey(resolvedKey, throwOnMissingSubKey: false);
            }
        }
        return Task.FromResult(StepResult.Ok());
    }
}
