using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// Headed wizard smoke test (T17). Launches the wizard's
/// <see cref="InstallerViewModel"/> on the Avalonia headless platform
/// (<c>Avalonia.Headless.XUnit</c>, so it runs in the normal <c>dotnet test</c>
/// loop on any box — no physical display, no CI-only virtual display gate) and
/// walks the REAL screen sequence Welcome → Location (destination) → Installing →
/// Done, driving the REAL step engine (<see cref="InstallEngine"/>) rather than a
/// fake runner. Because the engine actually executes its steps, the files a real
/// install lays down land on disk — the smoke test's end-to-end proof.
/// <para>
/// This complements <c>InstallFlowTests</c> (which injects a fake runner to unit
/// test the view-model's outcome routing): here the runner IS the production
/// engine, wired exactly as <c>App.OnFrameworkInitializationCompleted</c> wires
/// <c>InstallSession.RunInstallAsync</c> — a <see cref="IProgress{StepProgress}"/>
/// + <see cref="CancellationToken"/> delegate returning an
/// <see cref="InstallOutcome"/>.
/// </para>
/// </summary>
public sealed class WizardHeadedSmokeTests
{
    [AvaloniaFact]
    public async Task Wizard_walks_welcome_to_done_driving_the_real_engine()
    {
        using var temp = new TempWorkDir();

        // A real payload the engine will copy, and a real (writable) install dir.
        var payloadRoot = Path.Combine(temp.Root, "payload");
        Directory.CreateDirectory(payloadRoot);
        File.WriteAllText(Path.Combine(payloadRoot, "app.txt"), "hello from payload");
        var installDir = Path.Combine(temp.Root, "install");

        // Real install steps: make the install dir, copy a payload file into it.
        var steps = new InstallStep[]
        {
            new InstallStep.DirectoryCreate(
                Id: "mkdir-install",
                Path: installDir,
                When: null,
                OnFailure: OnFailure.Fail),
            // FileCopy's `To` is the destination DIRECTORY; the source file keeps
            // its name, landing at <installDir>/app.txt.
            new InstallStep.FileCopy(
                Id: "copy-app",
                From: "payload://app.txt",
                To: installDir,
                Overwrite: true,
                When: null,
                OnFailure: OnFailure.Fail),
        };

        var ctx = new StepContext(
            new Dictionary<string, object?>(),
            payloadRoot: payloadRoot,
            installDir: installDir);

        var engineRan = false;

        var vm = new InstallerViewModel(new BrandTokens { AppName = "Smoke App" });
        vm.ConfigureInstallRunner(async (progress, ct) =>
        {
            engineRan = true;
            // THE real engine — same type the stamped host drives via InstallSession.
            var result = await new InstallEngine().RunAsync(
                preInstall: Array.Empty<InstallStep>(),
                installSteps: steps,
                postInstall: Array.Empty<InstallStep>(),
                ctx: ctx,
                progress: progress,
                ct: ct).ConfigureAwait(true);
            return new InstallOutcome(result.Success, result.Error);
        });

        // Walk the real wizard flow (decision-4 default: Welcome → Location → Install).
        vm.CurrentStep.Should().Be(InstallerStep.Welcome);

        vm.Next(); // Welcome → Location (destination)
        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions);

        // Choose a real, writable destination so the Destination gate (T13) passes.
        vm.InstallPath = installDir;

        vm.Next(); // Location → Installing (fires the engine)
        // The Installing screen kicks off the real engine. (We don't assert the
        // transient Installing state here: the engine's tiny fixture install can
        // complete synchronously on the headless dispatcher, landing on Done before
        // control returns — so we assert the install actually started, then await it.)
        vm.InstallTask.Should().NotBeNull("entering the Installing screen starts the engine run");

        // Drive the real engine run to completion.
        await vm.InstallTask!;

        engineRan.Should().BeTrue("the wizard must drive the real InstallEngine, not a stub");
        vm.CurrentStep.Should().Be(InstallerStep.Finish, "a successful install lands on the Done screen");
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Completed);
        ((int)vm.OutcomeCode).Should().Be(0);

        // The real engine actually performed the filesystem work.
        File.Exists(Path.Combine(installDir, "app.txt")).Should().BeTrue(
            "the real engine copied the payload file into the install dir");
        File.ReadAllText(Path.Combine(installDir, "app.txt")).Should().Be("hello from payload");

        // And the Installing screen accumulated the engine's real progress log.
        // Progress<T> posts to the dispatcher, so the log lines arrive on turns
        // after InstallTask completes — poll briefly (as InstallFlowTests does).
        await WaitUntilAsync(() => vm.LogLines.Count > 0);
        vm.LogLines.Should().NotBeEmpty("the Installing screen shows the engine's real step log");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    /// <summary>A self-cleaning temp working directory for the smoke test.</summary>
    private sealed class TempWorkDir : IDisposable
    {
        public string Root { get; }

        public TempWorkDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "sigil-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
#pragma warning disable CA1031 // Best-effort temp cleanup.
            catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }
}
