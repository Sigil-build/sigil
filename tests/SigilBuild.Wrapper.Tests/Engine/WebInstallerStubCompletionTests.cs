using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// CRITICAL post-review fix (P12 / T12.5): a web-installer stub blob
/// (<c>WrapperBlob.IsDelegatingStub == true</c>) must NOT run
/// <c>InstallSession</c>'s success-path completion bookkeeping —
/// <c>ArpRegistration.Register</c>, <c>InstallSurvivability.InstallUninstaller</c>
/// (which copies the RUNNING exe as <c>uninstall.exe</c>), and
/// <c>UninstallStateStore.Save</c> — because the stub's only job is
/// http_download + run_program of the full package, which by the time
/// <c>run_program</c> returns has ALREADY run its own correct completion for the
/// SAME AppId/scope. Persisting again here would clobber the child's real
/// uninstall.json (with the stub's trivial two-step journal) and, when install
/// dirs coincide, the child's real uninstall.exe (with a copy of the stub) —
/// leaving Programs &amp; Features showing the app with an uninstaller that can
/// never actually remove it.
/// </summary>
/// <remarks>
/// Windows-only (real HKCU registry + <c>InstallSurvivability</c>'s file copy);
/// a no-op elsewhere, mirroring <c>ReinstallIdempotencyTests</c>. Each test uses
/// a throwaway, uniquely-named AppId so it can never collide with — or corrupt —
/// a real installed app's ARP row.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WebInstallerStubCompletionTests
{
    private const string UninstallRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// A benign, non-network step (rather than the production stub's real
    /// http_download + run_program) — deliberately, so this test exercises the
    /// SMALLEST seam that actually matters: whether
    /// <c>IsDelegatingStub</c> gates <c>PersistCompletion</c>, independent of
    /// which install steps happened to run before it.
    /// </summary>
    private static WrapperBlob Blob(string appId, bool isDelegatingStub, string markerDir) => new(
        AppId: appId,
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: new InstallStep[]
        {
            // R16: the marker directory is in an OS temp directory, never
            // install_dir, so the out-of-tree write is declared with the
            // production per-step opt-out. Under test here is the delegating
            // stub's completion bookkeeping.
            new InstallStep.DirectoryCreate("mk", markerDir, When: null, OnFailure.Fail)
                { AllowOutsideInstallDir = true },
        },
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: InstallScope.User,
        DisplayName: "Acme Web Stub",
        Publisher: "Acme, Inc.",
        Version: "3.2.0",
        EstimatedSizeBytes: 0,
        IsDelegatingStub: isDelegatingStub);

    private static async Task<(bool Success, string InstallDir)> InstallOnceAsync(WrapperBlob blob)
    {
        var parsed = CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        var outcome = await session.RunInstallCoreAsync(Array.Empty<byte>(), progress: null);
        var installDir = Path.Combine(ScopeLayout.For(InstallScope.User).InstallRoot, blob.AppId);
        return (outcome.Success, installDir);
    }

    [Fact]
    public async Task Delegating_stub_runs_its_steps_but_skips_ARP_uninstaller_copy_and_state_save()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "sigil.webstub." + Guid.NewGuid().ToString("N");
        var markerDir = Path.Combine(Path.GetTempPath(), "sigil-webstub-marker-" + Guid.NewGuid().ToString("N"));
        var blob = Blob(appId, isDelegatingStub: true, markerDir);

        try
        {
            var (success, installDir) = await InstallOnceAsync(blob);
            success.Should().BeTrue("the stub's own steps (here, a stand-in for http_download + run_program) still run and succeed");

            // The step itself ran — proves this isn't a no-op skip of the whole install.
            Directory.Exists(markerDir).Should().BeTrue("IsDelegatingStub only gates completion bookkeeping, not the steps themselves");

            // (a) NO ARP row — the child Setup.exe already registered one for this AppId.
            using (var arp = Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}"))
            {
                arp.Should().BeNull(
                    "a delegating stub must never write its OWN Add/Remove Programs row — the child Setup.exe already did");
            }

            // (b) NO uninstall.exe copied into the install dir — the child's real
            //     uninstaller (which can actually reverse the real install) must survive
            //     untouched, not get overwritten with a copy of the stub.
            File.Exists(Path.Combine(installDir, "uninstall.exe")).Should().BeFalse(
                "a delegating stub must never copy itself in as uninstall.exe over the child's real one");

            // (c) NO persisted uninstall state — the child already saved its own
            //     (correct, full) journal for this AppId/scope; the stub must not
            //     clobber it with its own trivial one.
            UninstallStateStore.TryLoad(appId, InstallScope.User).Should().BeNull(
                "a delegating stub must never overwrite the child's real uninstall.json with its own trivial journal");
        }
        finally
        {
            Cleanup(appId, markerDir);
        }
    }

    [Fact]
    public async Task Non_delegating_blob_still_persists_completion_normally()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Control case: proves the gate is a real branch, not an accidental
        // always-skip — a normal (non-stub) blob keeps registering ARP,
        // copying uninstall.exe, and saving uninstall state exactly as before.
        var appId = "sigil.webstub.control." + Guid.NewGuid().ToString("N");
        var markerDir = Path.Combine(Path.GetTempPath(), "sigil-webstub-marker-" + Guid.NewGuid().ToString("N"));
        var blob = Blob(appId, isDelegatingStub: false, markerDir);

        try
        {
            var (success, installDir) = await InstallOnceAsync(blob);
            success.Should().BeTrue();

            using (var arp = Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}"))
            {
                arp.Should().NotBeNull("a normal (non-delegating) install must still register its ARP row");
                arp!.GetValue("DisplayName").Should().Be("Acme Web Stub");
            }

            File.Exists(Path.Combine(installDir, "uninstall.exe")).Should().BeTrue(
                "a normal (non-delegating) install must still copy itself in as uninstall.exe");

            UninstallStateStore.TryLoad(appId, InstallScope.User).Should().NotBeNull(
                "a normal (non-delegating) install must still persist its uninstall state");
        }
        finally
        {
            Cleanup(appId, markerDir);
        }
    }

    private static void Cleanup(string appId, string markerDir)
    {
        var installDir = Path.Combine(ScopeLayout.For(InstallScope.User).InstallRoot, appId);
#pragma warning disable CA1031 // Best-effort test cleanup.
        try { ArpRegistration.Remove(appId, InstallScope.User); } catch { }
        try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { }
        try { if (Directory.Exists(markerDir)) Directory.Delete(markerDir, recursive: true); } catch { }
        try { if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true); } catch { }
#pragma warning restore CA1031
    }
}
