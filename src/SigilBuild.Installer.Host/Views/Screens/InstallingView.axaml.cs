using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views.Screens;

public partial class InstallingView : UserControl
{
    public InstallingView() { AvaloniaXamlLoader.Load(this); }

    /// <summary>
    /// When the screen becomes visible, kick off the actual install by
    /// spawning <c>setup.exe /S</c> (the wrapper's headless mode) as a child
    /// process. The wrapper passes its own <see cref="System.Environment.ProcessPath"/>
    /// via the <c>SIGIL_SETUP_EXE</c> env var when launching the wizard, so
    /// this view doesn't need to guess where the parent setup.exe lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why a subprocess rather than running install_steps in-wizard: the
    /// install engine lives in <c>SigilBuild.Wrapper</c> alongside resource-
    /// reading code that pulls <c>SIGIL_BLOB_V1</c> from a PE module via Win32
    /// resource APIs. Replicating that here would either duplicate the engine
    /// or require a shared-library refactor; spawning the wrapper as a child
    /// is one line and keeps the engine source-of-truth in one place. The
    /// child inherits UAC elevation via normal CreateProcess so
    /// <c>sc.exe create</c> / HKLM writes / Program Files writes succeed.
    /// </para>
    /// <para>
    /// Contract with the wrapper (see <see cref="SigilBuild.Wrapper.Program"/>):
    /// when the wrapper sees the wizard exit code 0, it knows the wizard's
    /// child <c>setup.exe /S</c> already ran install_steps and SKIPS its own
    /// engine call — preventing a double-install.
    /// </para>
    /// </remarks>
    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is InstallerViewModel vm)
        {
            try
            {
                await RunInstallAsync(vm);
            }
            catch (Exception ex)
            {
                InstallerLog.Error("InstallingView.RunInstallAsync threw", ex);
                vm.InstallCurrentItem = "Install failed: " + ex.Message;
            }
        }
    }

    private static async Task RunInstallAsync(InstallerViewModel vm)
    {
        var setupExe = Environment.GetEnvironmentVariable("SIGIL_SETUP_EXE");
        if (string.IsNullOrEmpty(setupExe) || !File.Exists(setupExe))
        {
            InstallerLog.Error($"SIGIL_SETUP_EXE not set or not found ('{setupExe ?? "<null>"}') — cannot run install");
            vm.InstallCurrentItem = "Setup binary not found";
            return;
        }
        InstallerLog.Info($"InstallingView: launching child '{setupExe}' /S for install_steps");
        vm.InstallProgress = 0;
        vm.InstallCurrentItem = "Starting install…";

        var psi = new ProcessStartInfo
        {
            FileName = setupExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/S");

        // Pass every install-time parameter override declared in the manifest
        // through to the child wrapper as /Name=Value. The wrapper's
        // CommandLineParser already accepts this form; values land in
        // ctx.Values which install_steps reference via ${parameters.<name>}.
        foreach (var kv in vm.ParameterValues)
        {
            // Empty values are skipped — the wrapper schema-default still
            // applies when a parameter is omitted from argv.
            if (string.IsNullOrEmpty(kv.Value)) continue;
            var arg = $"/{kv.Key}={kv.Value}";
            psi.ArgumentList.Add(arg);
            InstallerLog.Info($"  passing param: {arg}");
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for setup.exe /S");

        // Drain stdout/stderr concurrently so the child's pipe buffer never fills.
        var stdoutTask = DrainAsync(proc.StandardOutput, "child stdout: ");
        var stderrTask = DrainAsync(proc.StandardError, "child stderr: ");

        // Avalonia animates the progress while we wait. We don't have real
        // per-step granularity from the child yet — that needs an IPC channel
        // or stdout-protocol — so animate to ~90% over a few seconds and pin
        // there until the child exits. Then 100% on Finish transition.
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 90 && !proc.HasExited; i++)
            {
                await Task.Delay(60);
                var p = (i + 1) / 100.0;
                await Dispatcher.UIThread.InvokeAsync(() => vm.InstallProgress = p);
            }
        });

        await proc.WaitForExitAsync();
        await Task.WhenAll(stdoutTask, stderrTask);

        InstallerLog.Info($"InstallingView: child setup.exe /S exited with code {proc.ExitCode}");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.InstallProgress = 1.0;
            if (proc.ExitCode == 0)
            {
                vm.InstallCurrentItem = "Done";
                vm.CurrentStep = InstallerStep.Finish;
            }
            else
            {
                vm.InstallCurrentItem = $"Install failed (exit {proc.ExitCode}). See logs in the sigil-logs folder.";
                // Stay on the Installing screen with the failure message — the
                // user can read it and Cancel out. A dedicated error screen is
                // a follow-up.
            }
        });
    }

    private static async Task DrainAsync(StreamReader reader, string prefix)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                InstallerLog.Info(prefix + line);
            }
        }
        catch (Exception ex)
        {
            InstallerLog.Error($"drain failed for '{prefix}'", ex);
        }
    }
}
