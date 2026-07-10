namespace SigilBuild.Wrapper.Steps;

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// Promoted-MUST <c>service_install</c> step (was SHOULD-tier in the Sprint 5a
/// action catalog). Registers a Windows service via <c>sc.exe create</c>, then
/// optionally starts it. Records a <see cref="RollbackRecord.RemoveService"/>
/// BEFORE the create so a mid-install crash and <c>setup.exe /Uninstall</c>
/// both unwind the service.
/// </summary>
/// <remarks>
/// <para>
/// Uses sc.exe under the hood because the .NET ServiceController API can
/// query / control services but cannot create them. sc.exe is shipped with
/// every supported Windows SKU. Each <c>sc create</c> arg is passed via
/// <see cref="ProcessStartInfo.ArgumentList"/> so the runtime handles quoting
/// — the classic CommandLine quoting bugs around <c>binPath= "C:\…"</c> can't
/// recur here.
/// </para>
/// <para>
/// Service-not-found / sc.exe missing on uninstall are tolerated by the
/// rollback record; install-side failures (access denied, name conflict)
/// surface through <see cref="StepResult.Failed(string)"/> with sc.exe's
/// stderr included so operators can diagnose without re-running.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ServiceInstallStep : IStep
{
    private readonly InstallStep.ServiceInstall _spec;

    public ServiceInstallStep(InstallStep.ServiceInstall spec)
    {
        _spec = spec;
    }

    public async Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var name = ctx.Resolve(_spec.Name);
        var binaryPath = ctx.Resolve(_spec.BinaryPath);
        var displayName = string.IsNullOrEmpty(_spec.DisplayName) ? name : ctx.Resolve(_spec.DisplayName);
        var description = _spec.Description is null ? null : ctx.Resolve(_spec.Description);

        if (string.IsNullOrWhiteSpace(name))
        {
            return StepResult.Failed("service_install: name is empty after substitution");
        }
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return StepResult.Failed("service_install: binary_path is empty after substitution");
        }
        if (!File.Exists(binaryPath))
        {
            return StepResult.Failed(
                $"service_install '{name}': binary not found at '{binaryPath}'. " +
                "Check the install_steps run order — file_copy must complete before service_install.");
        }

        // Record the inverse BEFORE the create so an interrupted install
        // still tears the service down on /Uninstall.
        journal.Append(new RollbackRecord.RemoveService(name));

        // sc.exe create accepts key= value pairs (note the space after =).
        // ArgumentList emits each token quoted, so sc.exe sees:
        //   create "<name>" binPath= "<path>" start= <type> obj= <account> DisplayName= "<dn>"
        var createArgs = new System.Collections.Generic.List<string>
        {
            "create", name,
            "binPath=", binaryPath,
            "start=",  NormaliseStartType(_spec.StartType),
            "obj=",    NormaliseAccount(_spec.ServiceAccount),
            "DisplayName=", displayName,
        };
        var createResult = await RunScAsync(createArgs, ct).ConfigureAwait(false);
        if (createResult.ExitCode != 0)
        {
            return StepResult.Failed(
                $"service_install '{name}': sc.exe create exited {createResult.ExitCode}. " +
                $"Output: {(string.IsNullOrEmpty(createResult.Stderr) ? createResult.Stdout : createResult.Stderr)}");
        }

        if (!string.IsNullOrEmpty(description))
        {
            // sc description is a separate command and not strictly required;
            // a failure here is benign for the service itself, so log but
            // don't fail the install.
            await RunScAsync(new[] { "description", name, description! }, ct).ConfigureAwait(false);
        }

        if (_spec.StartAfterInstall)
        {
            var startResult = await RunScAsync(new[] { "start", name }, ct).ConfigureAwait(false);
            // sc.exe start exit codes:
            //   0     — start in progress
            //   1056  — service already running (treat as success)
            //   1058  — service disabled (treat as failure: the manifest asked for start but the
            //           start type is disabled — almost certainly a manifest bug)
            if (startResult.ExitCode != 0 && startResult.ExitCode != 1056)
            {
                return StepResult.Failed(
                    $"service_install '{name}': sc.exe start exited {startResult.ExitCode}. " +
                    $"Output: {(string.IsNullOrEmpty(startResult.Stderr) ? startResult.Stdout : startResult.Stderr)}");
            }
        }

        return StepResult.Ok();
    }

    private static string NormaliseStartType(string startType) => startType switch
    {
        "auto"     => "auto",
        "demand"   => "demand",
        "disabled" => "disabled",
        "boot"     => "boot",
        "system"   => "system",
        _          => "auto",
    };

    private static string NormaliseAccount(string account) => account switch
    {
        "LocalSystem"    => "LocalSystem",
        "NetworkService" => "NT AUTHORITY\\NetworkService",
        "LocalService"   => "NT AUTHORITY\\LocalService",
        _                => "LocalSystem",
    };

    private readonly record struct ScResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<ScResult> RunScAsync(System.Collections.Generic.IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for sc.exe");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return new ScResult(proc.ExitCode,
            (await stdoutTask.ConfigureAwait(false)).TrimEnd(),
            (await stderrTask.ConfigureAwait(false)).TrimEnd());
    }
}
