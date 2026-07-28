using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// Task 16 (P9) end-to-end fixtures: the silent-path invariance and fixed-
/// manifest-language legs of the localization mechanism (Tasks 1-15), packed
/// and run through a REAL spawned setup.exe — mirroring
/// <see cref="MultiEditionInstallTests"/> and <see cref="UpgradeInstallTests"/>
/// rather than inventing a new harness.
/// </summary>
/// <remarks>
/// <para><b>Layer exercised:</b> the full stack — <see cref="Sigil.PackAsync"/>
/// (the real <c>ExeWrapperPackager</c>, the same code path <c>sigil pack</c>
/// uses) produces an actual <c>Setup.exe</c>, which <see cref="VmSandbox"/>
/// then spawns as a real child process via <c>/silent</c>. This is the
/// genuinely end-to-end leg (unlike
/// <c>SigilBuild.Installer.Host.Tests.Localization.LocalizationEndToEndTests</c>,
/// which stops at the VM-render layer).</para>
/// <para><b>Gating</b> reports a genuine Skipped result (via
/// <see cref="VmFactAttribute"/>, register row R6) exactly like
/// <see cref="MultiEditionInstallTests"/>: not Windows, <c>SIGIL_VM_TESTS=1</c>
/// not set, or the Native-AOT-published <c>SigilBuild.Installer.Host</c>
/// runtime is not staged under <c>runtimes/win-x64/</c>
/// (<c>scripts/publish-installer-runtime.ps1</c> — requires the MSVC C++ Native
/// AOT linker, absent on this dev box). <see cref="TestEnvironment.IsRuntimeAvailable"/>
/// gates it exactly like the existing T13 VM-style tests.</para>
/// <para><b>Fixtures</b>: <c>localized-uk</c> (also used by the VM-render leg)
/// and <c>localized-uk-fixed</c> (the same manifest plus a fixed
/// <c>installer.language: en</c>), both under
/// <c>tests/SigilBuild.Packaging.IntegrationTests/Fixtures/</c> so both test
/// layers share one manifest source.</para>
/// </remarks>
public class LocalizationEndToEndTests
{
    private static string FindFixtureManifest(string fixtureName)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Sigil.slnx")))
            {
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }
        if (dir is null)
        {
            throw new InvalidOperationException("could not locate Sigil.slnx");
        }
        return Path.Combine(
            dir, "tests", "SigilBuild.Packaging.IntegrationTests", "Fixtures", fixtureName, "sigil.yaml");
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
    [VmFact]
    public async Task SilentInstall_IsUnaffectedByLang()
    {
        using var sandbox = new VmSandbox();
        var manifestPath = FindFixtureManifest("localized-uk");
        var outDir = Path.Combine(sandbox.Root, "out");
        var setupExe = await Sigil.PackAsync(manifestPath, outDir);

        var enDir = Path.Combine(sandbox.Root, "install-en");
        var ukDir = Path.Combine(sandbox.Root, "install-uk");
        var enLog = Path.Combine(sandbox.Root, "en.log");
        var ukLog = Path.Combine(sandbox.Root, "uk.log");

        var enExit = await sandbox.RunAsync(setupExe, "/silent", $"/D={enDir}", $"/LOG={enLog}");
        var ukExit = await sandbox.RunAsync(setupExe, "/silent", "/lang=uk", $"/D={ukDir}", $"/LOG={ukLog}");

        ukExit.Should().Be(enExit).And.Be(0);

        var enFiles = ListRelativeFiles(enDir);
        var ukFiles = ListRelativeFiles(ukDir);
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
        // timestamp on every line and the header's args=[...] echo (which legitimately
        // reflects each run's own /D, /LOG and /lang flags) — strip exactly those and
        // require byte-for-byte equality of everything else.
        var enBody = StripTimestampsAndArgsHeader(enLogText);
        var ukBody = StripTimestampsAndArgsHeader(ukLogText);

        // Sanity check the fixture actually exercises the happy path (so the
        // comparison above isn't vacuously comparing two near-empty logs).
        enLogText.Should().Contain("result: success", "the fresh install must complete");
        ukLogText.Should().Contain("result: success", "the fresh install must complete");

        ukBody.Should().Be(
            enBody,
            "the log wording must be identical regardless of /lang (design D2 - the log " +
            "is the support surface and stays English) once timestamps and the args-echo " +
            "header are stripped");
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
    /// LOGGED, never fatal. Exit code stays 0 (unlike T12's fixed-scope-vs-
    /// <c>/allusers</c> rule, which exits 64 — scope is a trust boundary,
    /// language is a display preference).
    /// </summary>
    [VmFact]
    public async Task FixedManifestLanguage_LogsAndIgnoresLangFlag()
    {
        using var sandbox = new VmSandbox();
        var manifestPath = FindFixtureManifest("localized-uk-fixed");
        var outDir = Path.Combine(sandbox.Root, "out");
        var setupExe = await Sigil.PackAsync(manifestPath, outDir);

        var installDir = Path.Combine(sandbox.Root, "install");
        var logPath = Path.Combine(sandbox.Root, "run.log");

        var exit = await sandbox.RunAsync(
            setupExe, "/silent", "/lang=uk", $"/D={installDir}", $"/LOG={logPath}");

        exit.Should().Be(0, "a language conflict is not a usage error (design §2.1)");

        File.Exists(logPath).Should().BeTrue("/LOG was requested");
        var logText = File.ReadAllText(logPath);
        logText.Should().Contain("manifest pin 'en' overrides /lang=uk");
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
