namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// Register row R28 — the <c>.sigil-bak</c> contract.
/// </summary>
/// <remarks>
/// <para>
/// <c>FileCopyStep</c> and <c>HttpDownloadStep</c> copy any file they are about to
/// overwrite to <c>&lt;destination&gt;.sigil-bak</c> and journal a <c>restore_file</c>
/// record pointing at it. <c>DiscardTransientStashes</c> deliberately does not touch
/// those records, and that is CORRECT — the stash is the pre-existing content of a file
/// the publisher never shipped, and it is the only thing that lets uninstall put it back.
/// The defect was the missing lifecycle, not the retention: the copies sat in Program
/// Files, beside the files they shadow, for the whole life of the install.
/// </para>
/// <para>
/// The contract chosen: keep them, and move them at commit into
/// <c>&lt;StateRoot&gt;\Sigil\&lt;AppId&gt;\backups</c> — out of the install directory,
/// inside the state directory S1 hardened, and inside the same anchored replay root the
/// uninstall already trusts.
/// </para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class StashLifecycleTests
{
    [WindowsFact]
    public async Task A_committed_install_leaves_no_sigil_bak_in_the_install_directory()
    {
        using var installDir = new TempDir();
        using var payload = new TempDir();

        // A file the install is about to overwrite — a config the user already had.
        var target = Path.Combine(installDir.Path, "app.config");
        await File.WriteAllTextAsync(target, "the user's own settings");

        var shipped = Path.Combine(payload.Path, "app.config");
        await File.WriteAllTextAsync(shipped, "the publisher's defaults");

        var appId = "com.acme.stash-" + Guid.NewGuid().ToString("N");
        try
        {
            await RunInstallAsync(appId, shipped, installDir.Path);

            Directory.EnumerateFiles(installDir.Path, "*.sigil-bak", SearchOption.AllDirectories)
                .Should().BeEmpty(
                    "a committed install must not leave the previous contents of the files it " +
                    "overwrote sitting beside them in the install directory for the whole life " +
                    "of the app");

            (await File.ReadAllTextAsync(target)).Should().Be("the publisher's defaults");

            // ...and the stash is not gone, it moved.
            var stashDir = UninstallStateStore.StashDirectoryFor(appId, InstallScope.User);
            Directory.Exists(stashDir).Should().BeTrue("the stash has a home, it was not discarded");
            Directory.EnumerateFiles(stashDir).Should().ContainSingle(
                "exactly one file was overwritten");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    /// <summary>
    /// The capability the relocation exists to preserve, and the reason "just delete
    /// them" was the wrong contract: uninstall must still restore the file the install
    /// overwrote, byte for byte, from its new home.
    /// </summary>
    [WindowsFact]
    public async Task Uninstall_still_restores_the_file_the_install_overwrote()
    {
        using var installDir = new TempDir();
        using var payload = new TempDir();

        var target = Path.Combine(installDir.Path, "app.config");
        await File.WriteAllTextAsync(target, "the user's own settings");

        var shipped = Path.Combine(payload.Path, "app.config");
        await File.WriteAllTextAsync(shipped, "the publisher's defaults");

        var appId = "com.acme.stash-" + Guid.NewGuid().ToString("N");
        try
        {
            await RunInstallAsync(appId, shipped, installDir.Path);
            (await File.ReadAllTextAsync(target)).Should().Be("the publisher's defaults");

            var result = await new UninstallEngine().RunAsync(appId, installDir.Path, InstallScope.User);

            result.Success.Should().BeTrue(result.Error ?? "clean uninstall");
            File.Exists(target).Should().BeTrue(
                "the file existed before the install, so uninstall restores it rather than " +
                "deleting it");
            (await File.ReadAllTextAsync(target)).Should().Be(
                "the user's own settings",
                "the stash moved out of the install directory, and the replay must still find " +
                "it there — a relocation that lost the restore would be the regression this " +
                "contract exists to avoid");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    private static async Task RunInstallAsync(string appId, string sourceFile, string installDir)
    {
        var blob = new WrapperBlob(
            AppId: appId,
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: new InstallStep[]
            {
                new InstallStep.FileCopy(
                    "copy-config",
                    From: sourceFile,
                    To: installDir,
                    Overwrite: true,
                    When: null,
                    OnFailure: OnFailure.Fail),
            },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());

        var parsed = CommandLineParser.Parse(
            new[] { "/silent", "/D=" + installDir }, Array.Empty<ParameterDefinition>());
        var session = InstallSession.ForTesting(blob, parsed);

        var exit = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());
        exit.Should().Be(0, "the install must succeed for the commit-time relocation to run");
    }

    private static void Cleanup(string appId)
    {
#pragma warning disable CA1031 // Test cleanup must never mask the original assertion failure.
        try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { /* best-effort */ }
        try { ArpRegistration.Remove(appId, InstallScope.User); } catch { /* best-effort */ }
        try
        {
            var dir = UninstallStateStore.DirectoryFor(appId, InstallScope.User);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }
}
