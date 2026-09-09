using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// R58 (release blocker found at the G2 gate check) — the end-to-end proof that the
/// registered Add/Remove Programs <c>UninstallString</c> actually completes. Installs a
/// fixture, deletes the original <c>Setup.exe</c> (T15), then runs the ARP string
/// <em>verbatim</em> out of the registry, which means running
/// <c>&lt;install_dir&gt;\uninstall.exe</c> from INSIDE the directory the P6 files-in-use
/// gate sweeps.
/// </summary>
/// <remarks>
/// <para><b>Why this test did not exist.</b> The matrix looked like it covered this and
/// did not. <see cref="WixClassInstallUninstallTests"/> uninstalls through the ORIGINAL
/// packed exe and says so in a comment that calls it "the same code path the ARP
/// UninstallString invokes" — which is exactly the assumption R58 broke: the original
/// setup exe lives outside <c>install_dir</c>, so it never trips the gate, while the
/// dropped <c>uninstall.exe</c> always does. And
/// <c>wrapper-vm-tests.yml</c>'s <c>SIGIL_VM_UNINSTALL_SURVIVE</c> toggle, declared for
/// this very T15 scenario, was read by no test at all. Result: exit 4,
/// <c>blocked by: installer (pid N)</c> with N the uninstaller's own pid, on a shipped
/// installer whose user no longer has the original Setup.exe.</para>
///
/// <para><b>Where it runs.</b> The VM matrix only
/// (<see cref="VmUninstallSurviveFactAttribute"/>: Windows + <c>SIGIL_VM_TESTS=1</c> +
/// <c>SIGIL_VM_UNINSTALL_SURVIVE=1</c> + the staged AOT host runtime). It reports a
/// genuine Skipped result otherwise (register row R6) and cannot run on a dev box
/// without the staged runtime. The unit-level claim — that the Restart Manager sweep
/// never reports the running installer, while still reporting every other holder,
/// including an app whose image lives in the install dir — is covered by
/// <c>FilesInUseTests</c>, which runs everywhere.</para>
///
/// <para>Per-user scope throughout, so nothing here needs elevation or touches HKLM. The
/// machine-scope half of R58 (the un-elevated <c>ShellExecuteExW</c> relaunch parent,
/// alive with the same image loaded while the elevated child gates) is asserted at the
/// unit level instead; reproducing it end-to-end would require a UAC prompt.</para>
/// </remarks>
public sealed class ArpUninstallStringTests
{
    private const string UninstallRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    [VmUninstallSurviveFact]
    [SupportedOSPlatform("windows")]
    public async Task Registered_uninstall_string_completes_from_inside_the_install_dir()
    {
        using var sandbox = new VmSandbox();
        var appId = NewAppId();
        var installDir = Path.Combine(sandbox.Root, "app");
        try
        {
            // Arrange — install, then take away the original setup exe (T15's premise:
            // the user has deleted their download and only the dropped copy remains).
            var setupExe = await PackFixtureAsync(sandbox, appId, installDir).ConfigureAwait(false);
            (await sandbox.RunAsync(setupExe, "/S", "/currentuser").ConfigureAwait(false))
                .Should().Be(0, "the fixture must install before its uninstall can be judged");

            var payload = Path.Combine(installDir, "app.txt");
            File.Exists(payload).Should().BeTrue("the payload landed");

            var uninstallString = ReadArp(appId, "UninstallString");
            uninstallString.Should().NotBeNullOrWhiteSpace("the install registered an ARP row");

            var (exe, args) = SplitCommandLine(uninstallString!);
            Path.GetFullPath(exe).Should().StartWith(
                Path.GetFullPath(installDir),
                "R58 only bites because T15 puts the uninstaller INSIDE the swept directory " +
                "— if this ever stops being true, re-read the defect before relaxing anything");

            File.Delete(setupExe);

            // Act — exactly what Add/Remove Programs runs, no substitutions.
            var rc = await RunAsync(exe, args).ConfigureAwait(false);

            // Assert
            rc.Should().Be(
                0,
                "the registered UninstallString must complete; before R58 it exited 4 " +
                "(FilesInUseExitCode) because the P6 gate counted the running uninstaller " +
                "itself as a blocker — 'blocked by: installer (pid N)' with N its own pid");
            File.Exists(payload).Should().BeFalse("the uninstall removed the payload");
            ReadArp(appId, "DisplayName").Should().BeNull("the uninstall removed the ARP row");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    /// <summary>
    /// Split a registered <c>UninstallString</c> into its exe and its arguments. The
    /// value Sigil writes is <c>"&lt;path&gt;" /S /Uninstall /currentuser</c>
    /// (<c>ArpRegistration.BuildUninstallString</c>), i.e. a quoted path followed by
    /// space-separated flags — nothing that needs full CommandLineToArgvW rules.
    /// </summary>
    private static (string Exe, string[] Args) SplitCommandLine(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (!trimmed.StartsWith('"'))
        {
            throw new InvalidOperationException(
                $"expected a quoted exe path in the UninstallString, got: {commandLine}");
        }

        var closing = trimmed.IndexOf('"', 1);
        if (closing < 0)
        {
            throw new InvalidOperationException(
                $"unterminated quote in the UninstallString: {commandLine}");
        }

        var exe = trimmed[1..closing];
        var args = trimmed[(closing + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (exe, args);
    }

    /// <summary>
    /// Run the uninstaller from its OWN directory, the way the shell launches an ARP
    /// entry. Not <see cref="VmSandbox.RunAsync"/>, which anchors the working directory
    /// at the sandbox root — the working directory is a handle inside
    /// <c>install_dir</c> and therefore part of what R58 is about.
    /// </summary>
    private static async Task<int> RunAsync(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start '{exe}'");
        await p.WaitForExitAsync().ConfigureAwait(false);
        return p.ExitCode;
    }

    /// <summary>
    /// A schema-valid unique app id for one run. R66: mirroring
    /// <c>UpgradeInstallTests</c>, this used to be <c>"com.sigil.r58." + Guid("N")</c>,
    /// whose trailing hex segment usually starts with a digit — which <c>app.id</c>'s
    /// letter-led-segment pattern rejects. This test has never run on a VM (it landed
    /// in PR #39, after the run that exposed the same defect in its siblings), so the
    /// same rot was already baked in before its first execution.
    /// </summary>
    internal static string NewAppId() => "com.sigil.r58.r" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Build the fixture manifest YAML. Pure: no sandbox, no disk, no packer — so the
    /// always-on <see cref="VmFixtureManifestTests"/> can validate the exact string
    /// this VM leg packs without a staged runtime (register row R66).
    /// </summary>
    internal static string BuildManifestYaml(string appId, string installDir)
    {
        // $$ raw string: {{...}} interpolates, single braces ({install_dir}) are literal.
        // Sigil.YamlQuote emits a single-quoted scalar, so the install dir's backslashes
        // need no hand-doubling — and cannot become an unknown-escape parse error (R66).
        //
        // `to:` is a destination DIRECTORY, never a file name — FileCopyStep does
        // Directory.CreateDirectory(to) and then Path.Combine(to, <relative path>) per
        // match, with no single-source-to-single-file branch. This fixture used to say
        // `to: '{install_dir}\app.txt'`, which made app.txt a DIRECTORY holding
        // app.txt\app.txt: exit 0, correct ARP row, and File.Exists below false against
        // a directory. Guarded now by
        // VmFixtureManifestTests.Vm_fixture_file_copy_destinations_are_directories.
        return $$"""
spec: v1.0

app:
  id: {{appId}}
  name: SigilR58Fixture
  version: 1.0.0
  publisher: SigilBuild

build:
  source: ./payload

package:
  formats: [exe]
  architectures: [x64]

installer:
  scope: auto
  install_dir: {{Sigil.YamlQuote(installDir)}}

install_steps:
  - id: copy-app
    type: file_copy
    from: 'payload://app.txt'
    to: '{install_dir}'
""";
    }

    /// <summary>
    /// A minimal self-contained fixture: one payload file copied to
    /// <paramref name="installDir"/>. Mirrors <c>UpgradeInstallTests.PackFixtureAsync</c>
    /// — <c>payload://</c> source and <c>{install_dir}</c> destination are the
    /// code-verified forms.
    /// </summary>
    private static async Task<string> PackFixtureAsync(
        VmSandbox sandbox, string appId, string installDir)
    {
        var fixtureDir = Path.Combine(sandbox.Root, "fixture");
        var payloadDir = Path.Combine(fixtureDir, "payload");
        Directory.CreateDirectory(payloadDir);
        await File.WriteAllTextAsync(Path.Combine(payloadDir, "app.txt"), "r58 payload\n")
            .ConfigureAwait(false);

        var manifestPath = Path.Combine(fixtureDir, "sigil.yaml");
        await File.WriteAllTextAsync(manifestPath, BuildManifestYaml(appId, installDir))
            .ConfigureAwait(false);

        return await Sigil.PackAsync(manifestPath, Path.Combine(fixtureDir, "out"))
            .ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadArp(string appId, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}");
        return key?.GetValue(valueName) as string;
    }

    /// <summary>
    /// Undo everything an install leaves outside the sandbox directory: the HKCU ARP
    /// subtree AND the per-user state dir under <c>%LocalAppData%\Sigil\&lt;appId&gt;</c>,
    /// which lives outside <see cref="VmSandbox"/>'s root and so survives its disposal.
    /// A failed run must not leave residue on the runner for the next leg to trip over.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void Cleanup(string appId)
    {
#pragma warning disable CA1031 // best-effort test cleanup
        try { Registry.CurrentUser.DeleteSubKeyTree($@"{UninstallRoot}\{appId}", throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
        try { UninstallStateStore.Delete(appId, InstallScope.User); }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }
}
