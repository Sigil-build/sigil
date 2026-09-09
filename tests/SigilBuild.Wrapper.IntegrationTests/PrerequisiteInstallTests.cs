using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using Xunit;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// P5 (gap G6) end-to-end prerequisite legs, appended to the wrapper VM matrix.
/// Packs a self-contained fixture whose prerequisite <c>source</c> is a bundled copy
/// of <c>cmd.exe</c> (the "fake redist"): it writes an HKCU registry value (the
/// detect target — no file-path quoting) and exits with a chosen code. Covers:
/// <list type="bullet">
///   <item><description>detect-false → install → exit 3010 → the silent install exits 3010;</description></item>
///   <item><description>detect-true → the prerequisite is skipped (a would-fail exe is never run);</description></item>
///   <item><description>exit code outside <c>exit_codes_ok</c> → abort before the journal (no app installed).</description></item>
/// </list>
/// Reports a genuine Skipped result (via <see cref="VmPrerequisiteFactAttribute"/>,
/// register row R6) unless Windows + <c>SIGIL_VM_TESTS=1</c> + <c>SIGIL_VM_PREREQ=1</c> +
/// the staged AOT runtime. The runner decision logic is additionally covered by fast
/// unit tests (<c>PrerequisiteRunnerTests</c>).
/// </summary>
public sealed class PrerequisiteInstallTests
{
    private const string DetectKeyRoot = @"Software\SigilPrereqTest";

    [VmPrerequisiteFact]
    [SupportedOSPlatform("windows")]
    public async Task Prerequisite_installs_and_exit_3010_makes_the_silent_install_exit_3010()
    {
        using var sandbox = new VmSandbox();
        var id = Guid.NewGuid().ToString("N");
        var detectKey = $@"{DetectKeyRoot}\{id}";
        try
        {
            var setup = await PackFixtureAsync(sandbox, id, detectKey, exitCode: 3010, exitCodesOk: "[0, 3010]");

            var rc = await sandbox.RunAsync(setup, "/S", "/currentuser", $"/D={sandbox.AppDir}");

            rc.Should().Be(3010, "an accepted prerequisite exit code of 3010 propagates as the silent reboot exit code");
            File.Exists(Path.Combine(sandbox.AppDir, "app.txt")).Should().BeTrue("the app installs after the prerequisite");
            DetectValuePresent(detectKey).Should().BeTrue("the fake prerequisite wrote its detect marker");
        }
        finally
        {
            CleanupDetectKey(detectKey);
        }
    }

    [VmPrerequisiteFact]
    [SupportedOSPlatform("windows")]
    public async Task Already_satisfied_prerequisite_is_skipped()
    {
        using var sandbox = new VmSandbox();
        var id = Guid.NewGuid().ToString("N");
        var detectKey = $@"{DetectKeyRoot}\{id}";
        try
        {
            // Pre-satisfy detect. The fake exe is wired to exit 9999 (NOT ok) — so if it
            // were run, the install would fail. A clean exit 0 proves it was skipped.
            using (var k = Registry.CurrentUser.CreateSubKey(detectKey))
            {
                k!.SetValue("Installed", 1);
            }

            var setup = await PackFixtureAsync(sandbox, id, detectKey, exitCode: 9999, exitCodesOk: "[0]");

            var rc = await sandbox.RunAsync(setup, "/S", "/currentuser", $"/D={sandbox.AppDir}");

            rc.Should().Be(0, "an already-satisfied prerequisite is skipped, so the would-fail exe never runs");
            File.Exists(Path.Combine(sandbox.AppDir, "app.txt")).Should().BeTrue();
        }
        finally
        {
            CleanupDetectKey(detectKey);
        }
    }

    [VmPrerequisiteFact]
    [SupportedOSPlatform("windows")]
    public async Task Prerequisite_exit_code_outside_ok_set_aborts_before_install()
    {
        using var sandbox = new VmSandbox();
        var id = Guid.NewGuid().ToString("N");
        var detectKey = $@"{DetectKeyRoot}\{id}";
        try
        {
            var setup = await PackFixtureAsync(sandbox, id, detectKey, exitCode: 1603, exitCodesOk: "[0]");

            var rc = await sandbox.RunAsync(setup, "/S", "/currentuser", $"/D={sandbox.AppDir}");

            rc.Should().NotBe(0, "a prerequisite exit code outside exit_codes_ok aborts the install");
            File.Exists(Path.Combine(sandbox.AppDir, "app.txt")).Should().BeFalse("the abort happens before the journal opens — no app files");
        }
        finally
        {
            CleanupDetectKey(detectKey);
        }
    }

    /// <summary>
    /// A schema-valid app id for one run. R66: the id used to be
    /// <c>"com.sigil.p5." + Guid("N")</c>, whose trailing hex segment usually starts
    /// with a digit, which <c>app.id</c>'s letter-led-segment pattern rejects — the
    /// manifest never reached the packer on the first real VM run. The <c>r</c> prefix
    /// fixes the segment; hex digits are otherwise pattern-legal.
    /// </summary>
    internal static string AppIdFor(string id) => "com.sigil.p5.r" + id;

    /// <summary>
    /// Build the fixture manifest YAML. Pure: no sandbox, no disk, no packer — so the
    /// always-on <see cref="VmFixtureManifestTests"/> can validate the exact string
    /// this VM leg packs without a staged runtime (register row R66).
    /// </summary>
    internal static string BuildManifestYaml(
        string id, string detectKey, int exitCode, string exitCodesOk)
    {
        // reg add HKCU\<detectKey> /v Installed /d 1 /f  — no spaces/quotes in the key.
        var cmdArg = $"reg add HKCU\\{detectKey} /v Installed /d 1 /f & exit /b {exitCode}";
        var detectExpr = $"registry_exists('HKCU', '{detectKey}', 'Installed')";

        // R66: `detectKey` and `cmdArg` both carry single backslashes, and both used to
        // be interpolated into DOUBLE-quoted YAML scalars, where `\S` is an unknown
        // escape — "While scanning a quoted scalar, found unknown escape character",
        // the error that killed all three of these legs on the first real VM run.
        // Sigil.YamlQuote emits single-quoted scalars, which have no escapes at all
        // (it doubles the apostrophes the detect expression itself contains).
        // $$ raw string: {{...}} interpolates, single braces ({install_dir}) are literal.
        //
        // `to:` is a destination DIRECTORY, never a file name — FileCopyStep does
        // Directory.CreateDirectory(to) and then Path.Combine(to, <relative path>) per
        // match, with no single-source-to-single-file branch. This fixture used to say
        // `to: '{install_dir}\app.txt'`, which made app.txt a DIRECTORY holding
        // app.txt\app.txt, so the two legs asserting File.Exists(app.txt) failed against
        // a directory while the install itself exited 0 / 3010 exactly as designed — and
        // the third leg, which asserts File.Exists is FALSE, passed vacuously. Guarded
        // now by VmFixtureManifestTests.Vm_fixture_file_copy_destinations_are_directories.
        return $$"""
spec: v1.0

app:
  id: {{AppIdFor(id)}}
  name: SigilP5Fixture
  version: 1.0.0
  publisher: SigilBuild

build:
  source: ./payload

package:
  formats: [exe]
  architectures: [x64]

installer:
  scope: auto
  prerequisites:
    - name: 'Fake Redist'
      detect: {{Sigil.YamlQuote(detectExpr)}}
      source: 'payload://prereq/fake.exe'
      args: ['/c', {{Sigil.YamlQuote(cmdArg)}}]
      exit_codes_ok: {{exitCodesOk}}

install_steps:
  - id: copy-app
    type: file_copy
    from: 'payload://app.txt'
    to: '{install_dir}'
""";
    }

    /// <summary>
    /// Write a fixture: a payload with an <c>app.txt</c> and a bundled copy of
    /// <c>cmd.exe</c> as the fake prerequisite, plus a manifest whose prerequisite runs
    /// that cmd to <c>reg add</c> the detect value (no path quoting) and exit with
    /// <paramref name="exitCode"/>. Packs it into a Setup.exe.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task<string> PackFixtureAsync(
        VmSandbox sandbox, string id, string detectKey, int exitCode, string exitCodesOk)
    {
        var fixtureDir = Path.Combine(sandbox.Root, "fixture-" + id);
        var payloadDir = Path.Combine(fixtureDir, "payload");
        var prereqDir = Path.Combine(payloadDir, "prereq");
        Directory.CreateDirectory(prereqDir);
        File.WriteAllText(Path.Combine(payloadDir, "app.txt"), "app\n");

        // The fake prerequisite installer is a copy of cmd.exe.
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        File.Copy(cmd, Path.Combine(prereqDir, "fake.exe"));

        var manifestPath = Path.Combine(fixtureDir, "sigil.yaml");
        await File.WriteAllTextAsync(
                manifestPath, BuildManifestYaml(id, detectKey, exitCode, exitCodesOk))
            .ConfigureAwait(false);

        var outDir = Path.Combine(fixtureDir, "out");
        return await Sigil.PackAsync(manifestPath, outDir).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static bool DetectValuePresent(string detectKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(detectKey);
        return key?.GetValue("Installed") is not null;
    }

    [SupportedOSPlatform("windows")]
    private static void CleanupDetectKey(string detectKey)
    {
#pragma warning disable CA1031 // best-effort test cleanup
        try { Registry.CurrentUser.DeleteSubKeyTree(detectKey, throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }
}
