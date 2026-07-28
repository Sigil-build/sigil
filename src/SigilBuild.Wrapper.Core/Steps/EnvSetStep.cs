namespace SigilBuild.Wrapper.Steps;

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// MUST-tier <c>env_set</c> step. Writes a Windows environment variable to
/// either <c>HKCU\Environment</c> (user scope) or
/// <c>HKLM\System\CurrentControlSet\Control\Session Manager\Environment</c>
/// (machine scope, requires admin), records the prior value into the
/// rollback journal BEFORE mutation, and broadcasts <c>WM_SETTINGCHANGE</c>
/// so Explorer / new processes pick up the change without a logoff.
///
/// Supports three actions: <c>set</c> replaces the value, <c>append</c>
/// appends with the configured separator, <c>prepend</c> prepends. For
/// append/prepend on an absent or empty prior value the step writes just
/// the new value (no leading/trailing separator).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class EnvSetStep : IStep
{
    private readonly InstallStep.EnvSet _spec;

    public EnvSetStep(InstallStep.EnvSet spec)
    {
        _spec = spec;
    }

    public Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(StepResult.Failed("env_set requires Windows"));
        }

        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(journal);

        var name = _spec.Name;
        var resolvedValue = ctx.Resolve(_spec.Value);

        // T12: a step scope of "auto" (the value auto-generated PATH steps use)
        // defers to the resolved install scope — machine env for a per-machine
        // install, user env for a per-user install. Explicit "user"/"machine"
        // stays authoritative. The resolved scope is what lands in the rollback
        // record so undo targets the same hive.
        var envScope = ResolveEnvScope(_spec.Scope, ctx);

        // Snapshot prior value for rollback.
        string? prior;
        bool previouslyAbsent;
        using (var key = OpenEnvKey(envScope, writable: false))
        {
            if (key is null)
            {
                return Task.FromResult(StepResult.Failed(
                    $"could not open env key for scope '{envScope}'"));
            }
            var v = key.GetValue(name);
            previouslyAbsent = v is null;
            prior = v?.ToString();
        }

        // Record rollback BEFORE mutation so a crash mid-write still leaves
        // the journal in a state that restores correctly.
        journal.Append(new RollbackRecord.RestoreEnv(envScope, name, prior, previouslyAbsent));

        var newValue = ComputeNewValue(_spec.Action, prior, resolvedValue, _spec.Separator);

        using (var key = OpenEnvKey(envScope, writable: true))
        {
            if (key is null)
            {
                return Task.FromResult(StepResult.Failed(
                    $"could not open env key for scope '{envScope}' (writable)"));
            }
            key.SetValue(name, newValue, RegistryValueKind.String);
        }

        EnvBroadcast.NotifySettingChange();
        return Task.FromResult(StepResult.Ok());
    }

    /// <summary>
    /// Pure value-construction helper for the <c>set</c>/<c>append</c>/<c>prepend</c>
    /// actions. Visible to tests via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal static string ComputeNewValue(string action, string? prior, string value, string separator) => action switch
    {
        "set" => value,
        "append" => string.IsNullOrEmpty(prior) ? value : prior + separator + value,
        "prepend" => string.IsNullOrEmpty(prior) ? value : value + separator + prior,
        _ => throw new ArgumentException($"unknown env_set action '{action}'"),
    };

    /// <summary>
    /// Map the manifest step's <c>scope:</c> onto a concrete <c>user</c>/<c>machine</c>
    /// env target. A literal <c>user</c>/<c>machine</c> is authoritative; <c>auto</c>
    /// (or an empty value) defers to the resolved install scope (T12).
    /// </summary>
    internal static string ResolveEnvScope(string specScope, StepContext ctx) => specScope switch
    {
        "user" => "user",
        "machine" => "machine",
        "auto" or "" => ctx.Layout.EnvScope,
        _ => specScope, // unknown value: let OpenEnvKey throw the clear error.
    };

    private static RegistryKey? OpenEnvKey(string scope, bool writable) => scope switch
    {
        "user" => Registry.CurrentUser.OpenSubKey("Environment", writable),
        "machine" => Registry.LocalMachine.OpenSubKey(
            @"System\CurrentControlSet\Control\Session Manager\Environment", writable),
        _ => throw new ArgumentException($"unknown env scope '{scope}'"),
    };
}

/// <summary>
/// Best-effort <c>WM_SETTINGCHANGE</c> broadcaster. Used by both
/// <see cref="EnvSetStep"/> and <see cref="RollbackRecord.RestoreEnv"/> so
/// the on-undo restore is also visible to running shells. Failures are
/// swallowed: the step has already mutated the registry, and a missed
/// broadcast only delays propagation until the next logon.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class EnvBroadcast
{
    private const int HwndBroadcast = unchecked((int)0xFFFF);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint TimeoutMs = 5000;

    public static void NotifySettingChange()
    {
#pragma warning disable CA1031 // Best-effort: any failure to broadcast must not fail the step.
        try
        {
            unsafe
            {
                fixed (char* p = "Environment")
                {
                    SendMessageTimeoutW(
                        new IntPtr(HwndBroadcast),
                        WmSettingChange,
                        IntPtr.Zero,
                        (IntPtr)p,
                        SmtoAbortIfHung,
                        TimeoutMs,
                        out _);
                }
            }
        }
        catch
        {
            // Best-effort.
        }
#pragma warning restore CA1031
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}
