namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// Cross-task regression for the install-dir divergence bug: the survivable
/// <c>uninstall.exe</c> copy and the ARP <c>UninstallString</c> MUST land in the
/// exact SINGLE install directory that T13's <see cref="InstallDirResolver"/>
/// resolved for the run (honoring <c>/D=</c>, the manifest <c>install_dir</c>, the
/// wizard-collected path, else <c>&lt;scope root&gt;\&lt;App.Name&gt;</c>) — the same
/// directory the install steps copied files into. The former code recomputed the
/// location as <c>ScopeLayout.InstallRoot + AppId</c>, so a <c>/D=</c> override put
/// files in one place and the uninstaller in another. These tests pin that they now
/// coincide (no divergence). Windows-only (real HKCU ARP write); a no-op elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallDirUnificationTests
{
    private const string UninstallRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static WrapperBlob Blob(string appId, string appName, InstallStep[] steps, string? installDirOverride = null) => new(
        AppId: appId,
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: steps,
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: InstallScope.User,
        AppName: appName,
        InstallDir: installDirOverride,
        DisplayName: "G3 App",
        Publisher: "Example, Inc.",
        Version: "1.2.3",
        EstimatedSizeBytes: 4096);

    private static async Task InstallAsync(WrapperBlob blob, params string[] args)
    {
        var parsed = CommandLineParser.Parse(args, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        var outcome = await session.RunInstallCoreAsync(Array.Empty<byte>(), progress: null);
        outcome.Success.Should().BeTrue("the synthetic install pipeline must complete");
    }

    private static string? ReadUninstallString(string appId)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}");
        return key?.GetValue("UninstallString") as string;
    }

    [Fact]
    public async Task D_override_lands_uninstaller_and_ARP_in_the_overridden_dir()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "com.example.G3App." + Guid.NewGuid().ToString("N");
        using var tmp = new TempDir();
        // A subdir the /D= override points at that NO install step creates — proving
        // the uninstaller copy itself makes the directory when needed.
        var overrideDir = Path.Combine(tmp.Path, "G3App");

        // The dir the T13 resolver computes for these exact inputs — what the STEPS use.
        var stepInstallDir = InstallDirResolver.Resolve(
            scope: InstallScope.User,
            appName: "G3App",
            appId: appId,
            manifestInstallDir: null,
            cliOverride: overrideDir,
            collected: null);

        try
        {
            await InstallAsync(Blob(appId, "G3App", Array.Empty<InstallStep>()), "/silent", "/D=" + overrideDir);

            var uninstaller = Path.Combine(stepInstallDir, "uninstall.exe");
            File.Exists(uninstaller).Should().BeTrue(
                "uninstall.exe must be copied INTO the /D= override dir, not %LocalAppData%\\Programs\\<AppId>");

            // No divergence: the uninstaller's directory IS the resolved step install dir.
            Path.GetDirectoryName(uninstaller).Should().Be(stepInstallDir);

            ReadUninstallString(appId).Should().Be(
                $"\"{uninstaller}\" /S /Uninstall /currentuser",
                "the ARP UninstallString must target the uninstaller inside the resolved install dir");
        }
        finally
        {
            Cleanup(appId, stepInstallDir);
        }
    }

    [Fact]
    public async Task Default_dir_lands_uninstaller_and_ARP_under_scope_root_App_Name()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "com.example.G3App." + Guid.NewGuid().ToString("N");
        var appName = "G3App-" + Guid.NewGuid().ToString("N");

        // No /D=, no manifest override → default <scope root>\<App.Name> (NOT <root>\<AppId>).
        var stepInstallDir = InstallDirResolver.Resolve(
            scope: InstallScope.User,
            appName: appName,
            appId: appId,
            manifestInstallDir: null,
            cliOverride: null,
            collected: null);
        stepInstallDir.Should().EndWith(appName, "the default install dir uses App.Name, not App.Id");

        try
        {
            await InstallAsync(Blob(appId, appName, Array.Empty<InstallStep>()), "/silent");

            var uninstaller = Path.Combine(stepInstallDir, "uninstall.exe");
            File.Exists(uninstaller).Should().BeTrue();
            Path.GetDirectoryName(uninstaller).Should().Be(stepInstallDir);

            ReadUninstallString(appId).Should().Be(
                $"\"{uninstaller}\" /S /Uninstall /currentuser");
        }
        finally
        {
            Cleanup(appId, stepInstallDir);
        }
    }

    [Fact]
    public async Task File_copy_step_and_uninstaller_share_one_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "com.example.G3App." + Guid.NewGuid().ToString("N");
        using var payloadSrc = new TempDir();
        using var tmp = new TempDir();
        var overrideDir = Path.Combine(tmp.Path, "G3App");

        File.WriteAllText(Path.Combine(payloadSrc.Path, "app.txt"), "hello");

        var steps = new InstallStep[]
        {
            new InstallStep.FileCopy(
                Id: "copy",
                From: Path.Combine(payloadSrc.Path, "*.txt"),
                To: "{install_dir}",
                Overwrite: true,
                When: null,
                OnFailure: OnFailure.Fail),
        };

        var stepInstallDir = InstallDirResolver.Resolve(
            scope: InstallScope.User,
            appName: "G3App",
            appId: appId,
            manifestInstallDir: null,
            cliOverride: overrideDir,
            collected: null);

        try
        {
            await InstallAsync(Blob(appId, "G3App", steps), "/silent", "/D=" + overrideDir);

            var copiedFile = Path.Combine(stepInstallDir, "app.txt");
            var uninstaller = Path.Combine(stepInstallDir, "uninstall.exe");

            // The critical unification: files AND the uninstaller are co-located.
            File.Exists(copiedFile).Should().BeTrue("the file_copy step lands the payload in {install_dir}");
            File.Exists(uninstaller).Should().BeTrue("uninstall.exe lands in the SAME {install_dir}");
            Path.GetDirectoryName(copiedFile).Should().Be(Path.GetDirectoryName(uninstaller),
                "there must be no divergence between where files land and where uninstall.exe lands");
        }
        finally
        {
            Cleanup(appId, stepInstallDir);
        }
    }

    private static void Cleanup(string appId, string installDir)
    {
#pragma warning disable CA1031 // Best-effort test cleanup.
        try { ArpRegistration.Remove(appId, InstallScope.User); } catch { }
        try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { }
        try { if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true); } catch { }
#pragma warning restore CA1031
    }
}
