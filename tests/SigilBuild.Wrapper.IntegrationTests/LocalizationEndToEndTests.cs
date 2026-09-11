using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// End-to-end fixtures for the silent-path invariance and fixed-manifest-language legs of
/// the localization mechanism, packed and run through a REAL spawned setup.exe —
/// mirroring <see cref="MultiEditionInstallTests"/> and <see cref="UpgradeInstallTests"/>
/// rather than inventing a new harness.
/// </summary>
/// <remarks>
/// <para>Exercises the full stack: <see cref="Sigil.PackAsync"/> (the real
/// <c>ExeWrapperPackager</c>, the same code path <c>sigil pack</c> uses) produces an
/// actual <c>Setup.exe</c>, which <see cref="VmSandbox"/> then spawns as a real child
/// process via <c>/silent</c> — unlike
/// <c>SigilBuild.Installer.Host.Tests.Localization.LocalizationEndToEndTests</c>, which
/// stops at the VM-render layer.</para>
/// <para>Reports a genuine Skipped result (via <see cref="VmFactAttribute"/>, R6) exactly
/// like <see cref="MultiEditionInstallTests"/>: not Windows, <c>SIGIL_VM_TESTS=1</c> not
/// set, or the Native-AOT-published <c>SigilBuild.Installer.Host</c> runtime not staged
/// under <c>runtimes/win-x64/</c> (<c>scripts/publish-installer-runtime.ps1</c> — requires
/// the MSVC C++ Native AOT linker, absent on this dev box).</para>
/// <para>Fixtures: <c>localized-uk</c> (also used by the VM-render leg) and
/// <c>localized-uk-fixed</c> (the same manifest plus a fixed
/// <c>installer.language: en</c>), both under
/// <c>tests/SigilBuild.Packaging.IntegrationTests/Fixtures/</c> so both test layers share
/// one manifest source.</para>
/// </remarks>
public class LocalizationEndToEndTests
{
    internal static string FindFixtureManifest(string fixtureName) => Sigil.RepoPath(
        "tests/SigilBuild.Packaging.IntegrationTests/Fixtures/" + fixtureName + "/sigil.yaml");

    /// <summary>
    /// The argv these legs install with — the single definition the always-on
    /// <see cref="VmFixtureManifestTests"/> parses through the REAL
    /// <c>CommandLineParser</c> so a grammar drift fails in every CI run.
    /// </summary>
    internal static string[] SilentInstallArgs(string? lang, string installDir, string logPath) =>
        lang is null
            ? new[] { "/silent", "/D=" + installDir, "/LOG=" + logPath }
            : new[] { "/silent", "/lang=" + lang, "/D=" + installDir, "/LOG=" + logPath };

    /// <summary>
    /// The app id declared by the on-disk <c>localized-uk</c> fixture — the one identity
    /// two installs must NOT share. Named here so
    /// <see cref="BuildManifestYaml"/> fails loudly if the fixture is ever re-identified.
    /// </summary>
    private const string DeclaredAppId = "com.example.localized";

    /// <summary>
    /// A schema-valid app id unique to one install. <c>app.id</c>'s pattern requires
    /// every dot-separated segment to be letter-led, which a bare <c>Guid("N")</c> hex
    /// string usually is not (R66) — hence the <c>r</c> prefix, the same convention the
    /// sibling VM legs use.
    /// </summary>
    internal static string NewAppId() => DeclaredAppId + ".r" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The <c>localized-uk</c> manifest exactly as it ships, with only its
    /// <c>app.id</c> replaced by <paramref name="appId"/> — so two installs of the same
    /// fixture are two independent applications rather than one application installed
    /// twice. Pure (no sandbox, no packer) apart from reading the fixture, so the
    /// always-on <see cref="VmFixtureManifestTests"/> validates the exact string this
    /// leg packs (R66).
    /// </summary>
    internal static string BuildManifestYaml(string appId)
    {
        var yaml = File.ReadAllText(FindFixtureManifest("localized-uk"));
        var declaration = "id: " + DeclaredAppId;
        var occurrences = yaml.Split(declaration).Length - 1;
        if (occurrences != 1)
        {
            throw new InvalidOperationException(
                $"expected exactly one '{declaration}' in the localized-uk fixture, found " +
                $"{occurrences} — the fixture's app id changed and this rewrite is stale");
        }
        return yaml.Replace(declaration, "id: " + appId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Copy the <c>localized-uk</c> fixture into the sandbox under
    /// <paramref name="subdirectory"/>, rewrite its app id to
    /// <paramref name="appId"/>, and pack it. The whole fixture directory is copied
    /// because the manifest addresses <c>./payload</c> and its two <c>LICENSE*.txt</c>
    /// files relative to itself.
    /// </summary>
    private static async Task<string> PackWithOwnAppIdAsync(
        VmSandbox sandbox, string subdirectory, string appId)
    {
        var source = Path.GetDirectoryName(FindFixtureManifest("localized-uk"))!;
        var fixtureDir = Path.Combine(sandbox.Root, subdirectory);
        CopyDirectory(source, fixtureDir);

        var manifestPath = Path.Combine(fixtureDir, "sigil.yaml");
        await File.WriteAllTextAsync(manifestPath, BuildManifestYaml(appId));

        return await Sigil.PackAsync(manifestPath, Path.Combine(fixtureDir, "out"));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>
    /// Undo what an install leaves OUTSIDE the sandbox: the HKCU ARP subtree and the
    /// per-user state dir under <c>%LocalAppData%\Sigil\&lt;appId&gt;</c>. Neither lives
    /// under <see cref="VmSandbox"/>'s root, so neither is removed by its disposal —
    /// and the state dir is precisely the record whose survival makes a later install of
    /// the same id take the re-install-cleanup path instead of a fresh one.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void Cleanup(string appId)
    {
#pragma warning disable CA1031 // best-effort test cleanup
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{appId}",
                throwOnMissingSubKey: false);
        }
        catch { /* best-effort */ }
        try { UninstallStateStore.Delete(appId, InstallScope.User); }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }

    private static string[] ListRelativeFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Design §2.1 / §4: language is a display preference, never a trust
    /// boundary. A <c>/silent</c> install under <c>/lang=uk</c> must produce the
    /// BYTE-IDENTICAL outcome (exit code, installed files) as the same install
    /// with no <c>/lang</c> at all — and the <c>/LOG</c> file must stay English,
    /// because it is the support surface (someone pasting it into a ticket must
    /// not need translation).
    /// </summary>
    /// <remarks>
    /// The two runs use distinct app ids: packed under ONE id, the second run's
    /// per-user state-store record for the first run would trigger the re-install-cleanup
    /// path instead of a fresh install, replaying the FIRST run's uninstall — deleting the
    /// first root's files and comparing an emptied tree against a populated one. Nothing
    /// about the invariant under test is <c>/lang</c>-specific: the installed outcome must
    /// not depend on <c>/lang</c>, so each run gets its own identity instead. The app id
    /// is stripped from the log comparison anyway — it appears only on the
    /// <c>=== sigil install log …</c> header line, which
    /// <see cref="StripTimestampsAndArgsHeader"/> drops.
    /// </remarks>
    [VmFact]
    [SupportedOSPlatform("windows")]
    public async Task SilentInstall_IsUnaffectedByLang()
    {
        using var sandbox = new VmSandbox();
        var enAppId = NewAppId();
        var ukAppId = NewAppId();
        try
        {
            var enSetup = await PackWithOwnAppIdAsync(sandbox, "fixture-en", enAppId);
            var ukSetup = await PackWithOwnAppIdAsync(sandbox, "fixture-uk", ukAppId);

            var enDir = Path.Combine(sandbox.Root, "install-en");
            var ukDir = Path.Combine(sandbox.Root, "install-uk");
            var enLog = Path.Combine(sandbox.Root, "en.log");
            var ukLog = Path.Combine(sandbox.Root, "uk.log");

            var enExit = await sandbox.RunAsync(enSetup, SilentInstallArgs(null, enDir, enLog));
            var ukExit = await sandbox.RunAsync(ukSetup, SilentInstallArgs("uk", ukDir, ukLog));

            ukExit.Should().Be(enExit).And.Be(0);

            var enFiles = ListRelativeFiles(enDir);
            var ukFiles = ListRelativeFiles(ukDir);
            enFiles.Should().NotBeEmpty(
                "the two runs must be INDEPENDENT installs — an empty first root is the " +
                "signature of the second run's re-install cleanup replaying the first " +
                "run's uninstall, which is what sharing one app id between them caused");
            ukFiles.Should().BeEquivalentTo(enFiles, "the installed OUTCOME must not depend on /lang");

            File.Exists(enLog).Should().BeTrue("/LOG was requested");
            File.Exists(ukLog).Should().BeTrue("/LOG was requested");
            var enLogText = File.ReadAllText(enLog);
            var ukLogText = File.ReadAllText(ukLog);

            // A NotContain-a-Ukrainian-word check is non-discriminating here: the only
            // Ukrainian literal the engine ever writes ("Вилучення", InstallSession.cs
            // ~838) comes from the upgrade/downgrade-removal path, which never fires on
            // this fixture's fresh install (no prior version). It would pass just as
            // happily if the whole log were localized. Instead, prove the actual design
            // promise directly: the /lang=uk run and the plain run must produce the SAME
            // log wording. The only parts that are *expected* to differ are the
            // timestamp on every line and the header's args=[...] echo (which
            // legitimately reflects each run's own /D, /LOG, /lang — and now its own app
            // id) — strip exactly those and require byte-for-byte equality of the rest.
            var enBody = StripTimestampsAndArgsHeader(enLogText);
            var ukBody = StripTimestampsAndArgsHeader(ukLogText);

            // Sanity check the fixture actually exercises the happy path (so the
            // comparison above isn't vacuously comparing two near-empty logs).
            enLogText.Should().Contain("result: success", "the fresh install must complete");
            ukLogText.Should().Contain("result: success", "the fresh install must complete");

            ukBody.Should().Be(
                enBody,
                "the log wording must be identical regardless of /lang (ADR-015 §4 - the " +
                "log is the support surface and stays English) once timestamps and the " +
                "args-echo header are stripped");
        }
        finally
        {
            Cleanup(enAppId);
            Cleanup(ukAppId);
        }
    }

    private static readonly Regex TimestampPrefix = new(@"^\[[^\]]*\]\s*", RegexOptions.Compiled);

    /// <summary>
    /// Normalize a <c>/LOG</c> file's text for cross-run comparison: strip each
    /// line's <c>[UTC-ISO8601]</c> timestamp (written by the engine's install-log
    /// sink) and drop the header line entirely, since it echoes this run's own
    /// command-line flags (<c>/D</c>, <c>/LOG</c>, <c>/lang</c>) — legitimate,
    /// expected differences between the two runs, not a translation concern.
    /// </summary>
    private static string StripTimestampsAndArgsHeader(string logText)
    {
        var lines = logText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var stripped = TimestampPrefix.Replace(line, string.Empty);
            if (stripped.StartsWith("=== sigil ", StringComparison.Ordinal))
            {
                continue;
            }
            sb.Append(stripped).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Design §2.1: a manifest that fixes <c>installer.language</c> wins over a
    /// conflicting <c>/lang</c> flag — the flag is IGNORED and the conflict is
    /// LOGGED, never fatal. Exit code stays 0 (unlike the fixed-scope-vs-
    /// <c>/allusers</c> rule, which exits 64 — scope is a trust boundary,
    /// language is a display preference).
    /// </summary>
    [VmFact]
    [SupportedOSPlatform("windows")]
    public async Task FixedManifestLanguage_LogsAndIgnoresLangFlag()
    {
        using var sandbox = new VmSandbox();
        var manifestPath = FindFixtureManifest("localized-uk-fixed");
        var outDir = Path.Combine(sandbox.Root, "out");
        var setupExe = await Sigil.PackAsync(manifestPath, outDir);

        var installDir = Path.Combine(sandbox.Root, "install");
        var logPath = Path.Combine(sandbox.Root, "run.log");
        try
        {
            var exit = await sandbox.RunAsync(
                setupExe, SilentInstallArgs("uk", installDir, logPath));

            exit.Should().Be(0, "a language conflict is not a usage error (ADR-015 §2.1)");

            File.Exists(logPath).Should().BeTrue("/LOG was requested");
            var logText = File.ReadAllText(logPath);
            logText.Should().Contain("manifest pin 'en' overrides /lang=uk");
        }
        finally
        {
            // This fixture's app id is FIXED, so without this the ARP row and the state
            // dir outlive the run and the NEXT run of this test installs over a recorded
            // prior install — the re-install-cleanup path, replaying a stale uninstall
            // against a sandbox directory that no longer exists. Order- and
            // history-independence is not optional for a leg that runs on a shared
            // runner.
            Cleanup("com.example.localizedukfixed");
        }
    }

    [Fact]
    public void Test_environment_check_smoke()
    {
        // Always-runnable sanity test confirming the gate works as documented —
        // mirrors MultiEditionInstallTests.Test_environment_check_smoke.
        TestEnvironment.IsEnabled.Should().Be(
            Environment.GetEnvironmentVariable("SIGIL_VM_TESTS") == "1");
    }
}
