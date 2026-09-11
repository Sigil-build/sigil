using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using Xunit;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// The always-on half of the VM matrix: every fixture the <c>[VmFact]</c>-gated legs
/// pack, and every argv they pass to the packed Setup.exe, checked here through the
/// <em>real</em> manifest loader and the <em>real</em> command-line parser — with no
/// Windows Sandbox, no staged AOT runtime, and no <c>SIGIL_VM_*</c> toggle required
/// (R66).
/// </summary>
/// <remarks>
/// <para><b>Why this class exists.</b> The VM matrix was dispatch-only and had never
/// actually run. On its first real run, eleven of its nineteen tests failed, and not one
/// failure was about the behaviour under test — every one was the fixtures having rotted
/// underneath a suite nothing ever executed:</para>
/// <list type="number">
///   <item><description><b>Invalid YAML.</b> Paths and registry keys interpolated into
///   <em>double</em>-quoted scalars, where <c>\</c> is an escape character:
///   <c>manifest validation failed: While scanning a quoted scalar, found unknown escape
///   character</c>. Guarded by <see cref="Generated_vm_fixture_manifest_is_schema_valid"/>
///   — the fixture builders are pure functions returning the YAML, so the exact string a
///   VM leg packs is validated in every CI run.</description></item>
///   <item><description><b>Schema-invalid <c>app.id</c>.</b> A per-run unique id built
///   from a raw GUID hex segment, usually digit-led, against a pattern that requires
///   every segment to be letter-led. Guarded by
///   <see cref="Generated_app_ids_are_accepted_by_the_real_schema"/>, which exercises the
///   id <em>generators</em> repeatedly rather than one lucky draw.</description></item>
///   <item><description><b>Command-line grammar drift.</b> Flags like
///   <c>/Edition=enterprise</c> or <c>/install_dir=…</c> are not tokens the wrapper's
///   closed grammar accepts, so those legs exited <b>64</b> (usage error) before
///   installing anything. Guarded by
///   <see cref="Vm_leg_argv_parses_under_the_real_grammar"/>.</description></item>
///   <item><description><b><c>from: payload/**</c> instead of <c>payload://**</c></b>
///   (R59): schema-legal, so static validation cannot see it; at install time the glob
///   root resolves against the process working directory and the step fails with exit
///   <b>1</b>. Guarded by
///   <see cref="Vm_fixture_file_copy_sources_use_the_payload_scheme"/>.</description></item>
/// </list>
/// <para>What this class deliberately does NOT claim: that the VM legs pass. It proves
/// their inputs are well-formed and their invocations are accepted — the classes of
/// defect that made the first real run useless. Whether the install then behaves is the
/// VM matrix's own verdict, and only a dispatched <c>wrapper-vm-tests.yml</c> run can
/// give it.</para>
/// </remarks>
public sealed class VmFixtureManifestTests
{
    /// <summary>
    /// A stand-in for the install directories the VM legs really use, shaped like the
    /// GitHub runner's temp root and containing a <c>\S</c> sequence on purpose:
    /// <c>\S</c> is not a legal YAML double-quoted escape, so this value is exactly what
    /// a fixture writer must survive. Interpolated through <see cref="Sigil.YamlQuote"/>
    /// it is inert; interpolated into a double-quoted scalar it kills the parse.
    /// </summary>
    private const string RunnerShapedInstallDir = @"D:\a\sigil\sigil\_temp\Sigil VM\App";

    /// <summary>
    /// A registry key shaped like the one <c>PrerequisiteInstallTests</c> builds — the
    /// value whose <c>\S</c> produced the "unknown escape character" failure.
    /// </summary>
    private const string RunnerShapedRegistryPath = @"Software\SigilPrereqTest\deadbeefcafe";

    /// <summary>
    /// Every manifest a VM leg generates at run time, keyed by a stable, readable case
    /// name (the YAML itself must not be the theory key — it would become the test's
    /// display name). Built through the same functions the VM legs pack.
    /// </summary>
    private static Dictionary<string, string> GeneratedFixtures() => new(StringComparer.Ordinal)
    {
        ["UpgradeInstallTests/v1-dirA"] = UpgradeInstallTests.BuildManifestYaml(
            UpgradeInstallTests.NewAppId(), "1.0.0", RunnerShapedInstallDir + @"\A"),
        ["UpgradeInstallTests/v2-dirB"] = UpgradeInstallTests.BuildManifestYaml(
            UpgradeInstallTests.NewAppId(), "2.0.0", RunnerShapedInstallDir + @"\B"),
        ["ArpUninstallStringTests/r58"] = ArpUninstallStringTests.BuildManifestYaml(
            ArpUninstallStringTests.NewAppId(), RunnerShapedInstallDir),
        ["PrerequisiteInstallTests/exit-3010-ok"] = PrerequisiteInstallTests.BuildManifestYaml(
            "deadbeefcafe", RunnerShapedRegistryPath, 3010, "[0, 3010]"),
        ["PrerequisiteInstallTests/exit-9999-not-ok"] = PrerequisiteInstallTests.BuildManifestYaml(
            "deadbeefcafe", RunnerShapedRegistryPath, 9999, "[0]"),
        ["PrerequisiteInstallTests/exit-1603-not-ok"] = PrerequisiteInstallTests.BuildManifestYaml(
            "deadbeefcafe", RunnerShapedRegistryPath, 1603, "[0]"),
        ["LocalizationEndToEndTests/localized-uk-per-run-id"] =
            LocalizationEndToEndTests.BuildManifestYaml(LocalizationEndToEndTests.NewAppId()),
    };

    public static TheoryData<string> GeneratedFixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in GeneratedFixtures().Keys)
        {
            data.Add(name);
        }
        return data;
    }

    /// <summary>
    /// The manifests the VM legs pack from disk, addressed exactly as those legs address
    /// them (so a moved or renamed fixture fails here too).
    /// </summary>
    private static Dictionary<string, string> OnDiskFixtures() => new(StringComparer.Ordinal)
    {
        ["MultiEditionInstallTests"] = MultiEditionInstallTests.FindManifest(),
        ["WixClassInstallUninstallTests"] = WixClassInstallUninstallTests.FindManifest(),
        ["LocalizationEndToEndTests/localized-uk"] =
            LocalizationEndToEndTests.FindFixtureManifest("localized-uk"),
        ["LocalizationEndToEndTests/localized-uk-fixed"] =
            LocalizationEndToEndTests.FindFixtureManifest("localized-uk-fixed"),
    };

    public static TheoryData<string> OnDiskFixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in OnDiskFixtures().Keys)
        {
            data.Add(name);
        }
        return data;
    }

    /// <summary>
    /// Every fixture a VM leg packs — generated in-process <em>and</em> read from disk —
    /// under one theory key, so a step-shape check written once covers both halves.
    /// </summary>
    /// <remarks>
    /// The <c>from:</c> and <c>to:</c> guards below draw on this rather than on
    /// <see cref="OnDiskFixtureNames"/>. That distinction is the whole reason the
    /// <c>file_copy.to</c> defect survived this class's first version: the only
    /// step-shape guard it shipped ran over the on-disk half alone, while three of the
    /// four file-shaped destinations lived in the generated half and were never looked
    /// at. A guard that covers a subset of the fixtures advertises coverage it does not
    /// have — the same failure shape as R64.
    /// </remarks>
    public static TheoryData<string> AllFixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in GeneratedFixtures().Keys)
        {
            data.Add(name);
        }
        foreach (var name in OnDiskFixtures().Keys)
        {
            data.Add(name);
        }
        return data;
    }

    /// <summary>
    /// Every (manifest, argv) pair a VM leg actually invokes, drawn from the leg's own
    /// argv definition rather than restated here — a drift in the leg drifts this guard
    /// with it instead of past it.
    /// </summary>
    private static Dictionary<string, (string ManifestPath, string[] Args)> LegInvocations()
    {
        const string Dir = @"C:\Sigil VM\App";
        const string Log = @"C:\Sigil VM\run.log";
        var multi = MultiEditionInstallTests.FindManifest();
        var wix = WixClassInstallUninstallTests.FindManifest();
        var uk = LocalizationEndToEndTests.FindFixtureManifest("localized-uk");
        var ukFixed = LocalizationEndToEndTests.FindFixtureManifest("localized-uk-fixed");

        return new Dictionary<string, (string, string[])>(StringComparer.Ordinal)
        {
            ["MultiEdition/enterprise"] =
                (multi, MultiEditionInstallTests.SilentInstallArgs("enterprise", Dir)),
            ["MultiEdition/community"] =
                (multi, MultiEditionInstallTests.SilentInstallArgs("community", Dir)),
            ["WixClass/install"] =
                (wix, WixClassInstallUninstallTests.SilentInstallArgs(Dir)),
            ["WixClass/uninstall"] =
                (wix, WixClassInstallUninstallTests.SilentUninstallArgs()),
            ["Localization/no-lang"] =
                (uk, LocalizationEndToEndTests.SilentInstallArgs(null, Dir, Log)),
            ["Localization/lang-uk"] =
                (uk, LocalizationEndToEndTests.SilentInstallArgs("uk", Dir, Log)),
            ["Localization/fixed-lang-vs-lang-uk"] =
                (ukFixed, LocalizationEndToEndTests.SilentInstallArgs("uk", Dir, Log)),
        };
    }

    public static TheoryData<string> LegInvocationNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in LegInvocations().Keys)
        {
            data.Add(name);
        }
        return data;
    }

    /// <summary>
    /// Failure class 1 + 2: the YAML a VM leg generates must parse and must satisfy the
    /// schema. <c>ManifestLoader.ValidateAsync</c> is the string-input form of the exact
    /// schema + typed-parser pipeline <c>ManifestLoader.LoadAsync</c> runs inside
    /// <see cref="Sigil.PackAsync"/>, so a green result here is the same verdict the
    /// packer gives.
    /// </summary>
    [Theory]
    [MemberData(nameof(GeneratedFixtureNames))]
    public async Task Generated_vm_fixture_manifest_is_schema_valid(string fixtureName)
    {
        // Arrange
        var yaml = GeneratedFixtures()[fixtureName];

        // Act
        var diagnostics = await ManifestLoader.ValidateAsync(yaml, fixtureName);

        // Assert
        Errors(diagnostics).Should().BeEmpty(
            "the VM leg '{0}' packs this manifest, so a fixture that no longer parses or " +
            "no longer satisfies the schema wastes a whole dispatched VM run. YAML was:\n{1}",
            fixtureName,
            yaml);
    }

    /// <summary>
    /// Failure class 2, deterministically. The rotted ids were only <em>usually</em>
    /// invalid — <c>Guid("N")</c> starts with a hex digit about five times in eight — so a
    /// single sampled id is a coin toss, not a guard. Exercising each generator many
    /// times makes a digit-led segment a certainty rather than a chance, and the check
    /// runs the id through the real schema instead of restating its pattern here.
    /// </summary>
    [Fact]
    public async Task Generated_app_ids_are_accepted_by_the_real_schema()
    {
        // Arrange
        const int Draws = 32;

        for (var i = 0; i < Draws; i++)
        {
            // Act + Assert (per draw, so a failure names the offending id)
            await AssertValidAsync(
                UpgradeInstallTests.BuildManifestYaml(
                    UpgradeInstallTests.NewAppId(), "1.0.0", RunnerShapedInstallDir),
                "UpgradeInstallTests.NewAppId");

            await AssertValidAsync(
                ArpUninstallStringTests.BuildManifestYaml(
                    ArpUninstallStringTests.NewAppId(), RunnerShapedInstallDir),
                "ArpUninstallStringTests.NewAppId");

            await AssertValidAsync(
                PrerequisiteInstallTests.BuildManifestYaml(
                    Guid.NewGuid().ToString("N"), RunnerShapedRegistryPath, 3010, "[0, 3010]"),
                "PrerequisiteInstallTests.AppIdFor");

            await AssertValidAsync(
                LocalizationEndToEndTests.BuildManifestYaml(
                    LocalizationEndToEndTests.NewAppId()),
                "LocalizationEndToEndTests.NewAppId");
        }
    }

    /// <summary>
    /// The on-disk manifests the VM legs pack must load through the real loader — same
    /// call <see cref="Sigil.PackAsync"/> makes, environment interpolation included.
    /// </summary>
    [Theory]
    [MemberData(nameof(OnDiskFixtureNames))]
    public async Task On_disk_vm_fixture_manifest_loads(string fixtureName)
    {
        // Arrange
        var path = OnDiskFixtures()[fixtureName];

        // Act
        var result = await ManifestLoader.LoadAsync(path, new ProcessEnvironmentReader());

        // Assert
        Errors(result.Diagnostics).Should().BeEmpty(
            "the VM leg '{0}' packs {1}", fixtureName, path);
        result.Manifest.Should().NotBeNull();
    }

    /// <summary>
    /// Failure class 4 (R59): a <c>file_copy</c> whose <c>from:</c>
    /// omits the <c>payload://</c> scheme is schema-legal and silently wrong — only that
    /// literal scheme is rebased onto the extracted payload root
    /// (<c>StepContext.ResolvePath</c>); anything else is resolved against the install
    /// process's working directory, and the step fails at run time with
    /// <c>glob root 'payload' does not exist</c> and exit 1. A brace token
    /// (<c>{install_dir}</c>, <c>{staging_dir}</c>, …) or a rooted path is a legitimate
    /// source; a bare relative one never is, in a fixture that ships its own payload.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFixtureNames))]
    public async Task Vm_fixture_file_copy_sources_use_the_payload_scheme(string fixtureName)
    {
        // Arrange
        var copies = await FileCopyStepsOfAsync(fixtureName);

        // Act + Assert
        copies.Should().NotBeEmpty("every VM fixture installs at least one payload file");
        foreach (var copy in copies)
        {
            var acceptable = copy.From.StartsWith("payload://", StringComparison.Ordinal)
                || copy.From.Contains('{', StringComparison.Ordinal)
                || System.IO.Path.IsPathRooted(copy.From);
            acceptable.Should().BeTrue(
                "step '{0}' in fixture '{1}' copies from '{2}': only the literal " +
                "'payload://' scheme is rebased onto the extracted payload (R59) — a bare " +
                "relative glob like 'payload/**' is schema-legal, resolves against the " +
                "install process's working directory, and fails at install time with exit 1",
                copy.Id,
                fixtureName,
                copy.From);
        }
    }

    /// <summary>
    /// The other half of the same contract, and the defect that closed the matrix's
    /// second real run: <b><c>file_copy.to</c> is a destination DIRECTORY, never a
    /// destination file.</b>
    /// </summary>
    /// <remarks>
    /// <para><c>FileCopyStep.ExecuteAsync</c> does <c>Directory.CreateDirectory(to)</c>
    /// and then, for every glob match, <c>Path.Combine(to, &lt;path relative to the glob
    /// root&gt;)</c> — there is no single-source-to-single-file branch anywhere in it. A
    /// file-shaped destination therefore creates a <em>directory</em> of that name and
    /// lands the payload one level too deep: <c>from: 'payload://app.txt'</c> with
    /// <c>to: '{install_dir}\app.txt'</c> yields
    /// <c>&lt;install_dir&gt;\app.txt\app.txt</c>. The step returns
    /// <c>StepResult.Ok()</c>, the install exits 0, the ARP row is correct — and every
    /// <c>File.Exists(&lt;install_dir&gt;\app.txt)</c> assertion in the matrix fails
    /// against a directory.</para>
    /// <para>The rule enforced here — no file extension on the destination's last
    /// segment — is deliberately <em>tighter</em> than the runtime contract: an
    /// extension-less file name (<c>to: '{install_dir}\LICENSE'</c>) would still slip
    /// past it, and a legitimately dotted directory name would be rejected. Both are
    /// acceptable for a fixture guard. An extension on the last segment is the shape
    /// that actually bit, every corrected fixture satisfies the rule, and every broken
    /// value violated it.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllFixtureNames))]
    public async Task Vm_fixture_file_copy_destinations_are_directories(string fixtureName)
    {
        // Arrange
        var copies = await FileCopyStepsOfAsync(fixtureName);

        // Act + Assert
        copies.Should().NotBeEmpty("every VM fixture installs at least one payload file");
        foreach (var copy in copies)
        {
            System.IO.Path.GetExtension(LastPathSegment(copy.To)).Should().BeEmpty(
                "step '{0}' in fixture '{1}' copies from '{2}' to '{3}': `file_copy.to` " +
                "is a destination DIRECTORY. FileCopyStep creates it with " +
                "Directory.CreateDirectory(to) and then writes every match to " +
                "Path.Combine(to, <relative path>) — it has no single-source-to-single-" +
                "file branch — so a file-shaped destination becomes a DIRECTORY of that " +
                "name and the payload lands one level too deep " +
                "(<install_dir>\\app.txt\\app.txt), with the step still returning Ok and " +
                "the install still exiting 0. Write '{{install_dir}}' or " +
                "'{{install_dir}}\\<subdir>' and let `from:` name the file",
                copy.Id,
                fixtureName,
                copy.From,
                copy.To);
        }
    }

    /// <summary>
    /// Failure class 3: the argv a VM leg passes to the packed Setup.exe must be accepted
    /// by the wrapper's closed grammar. <see cref="CommandLineParser.Parse"/> is the same
    /// parser <c>InstallSession</c> calls, fed the same parameter set the packer writes
    /// into the blob (<c>ExeWrapperPackager.ParametersToList</c> — every declared
    /// parameter, unfiltered), so a token the installer would reject with exit 64 is
    /// rejected here instead, in every CI run.
    /// </summary>
    /// <remarks>
    /// <c>customOptions</c> is passed as <c>null</c>: none of these fixtures declare a
    /// custom component, and null is the stricter choice — a future
    /// <c>/Poption.&lt;name&gt;</c> argv would fail here until this guard is taught about
    /// the fixture's component list, rather than passing vacuously.
    /// </remarks>
    [Theory]
    [MemberData(nameof(LegInvocationNames))]
    public async Task Vm_leg_argv_parses_under_the_real_grammar(string invocationName)
    {
        // Arrange
        var (manifestPath, args) = LegInvocations()[invocationName];
        var result = await ManifestLoader.LoadAsync(manifestPath, new ProcessEnvironmentReader());
        result.Manifest.Should().NotBeNull();
        var parameters = result.Manifest!.Parameters is null
            ? Array.Empty<ParameterDefinition>()
            : result.Manifest.Parameters.Values.ToArray();

        // Act
        var parse = () => CommandLineParser.Parse(args, parameters, customOptions: null);

        // Assert
        parse.Should().NotThrow<UsageException>(
            "the VM leg '{0}' runs the packed Setup.exe as `{1}`; an unrecognized token " +
            "makes the installer exit 64 before it installs anything",
            invocationName,
            string.Join(' ', args));
    }

    /// <summary>
    /// Parse one fixture named by <see cref="AllFixtureNames"/> into the typed manifest
    /// graph and return its <c>file_copy</c> steps. Generated fixtures go through
    /// <c>ManifestParser.Parse</c> (the typed-graph half of what
    /// <see cref="ManifestLoader.ValidateAsync"/> runs on the same string); on-disk ones
    /// go through the full <see cref="ManifestLoader.LoadAsync"/> pipeline, environment
    /// interpolation included — in both cases the same code <see cref="Sigil.PackAsync"/>
    /// runs, so the steps inspected here are the steps the packer writes into the blob.
    /// </summary>
    private static async Task<InstallStep.FileCopy[]> FileCopyStepsOfAsync(string fixtureName)
    {
        SigilManifest? manifest;
        if (GeneratedFixtures().TryGetValue(fixtureName, out var yaml))
        {
            manifest = ManifestParser.Parse(yaml, fixtureName).Manifest;
            manifest.Should().NotBeNull(
                "the generated fixture '{0}' must parse. YAML was:\n{1}", fixtureName, yaml);
        }
        else
        {
            var path = OnDiskFixtures()[fixtureName];
            manifest = (await ManifestLoader.LoadAsync(path, new ProcessEnvironmentReader()))
                .Manifest;
            manifest.Should().NotBeNull(
                "the on-disk fixture '{0}' ({1}) must load", fixtureName, path);
        }

        return AllSteps(manifest!).OfType<InstallStep.FileCopy>().ToArray();
    }

    /// <summary>
    /// The last segment of a manifest path <em>template</em> — brace tokens intact, both
    /// separators honoured, no filesystem access and no resolution of
    /// <c>{install_dir}</c>. <c>Path.GetFileName</c> is not a substitute: the fixtures
    /// carry <c>\</c> separators that would not split on a non-Windows test host.
    /// </summary>
    private static string LastPathSegment(string value)
    {
        var index = value.AsSpan().LastIndexOfAny(PathSeparators);
        return index < 0 ? value : value[(index + 1)..];
    }

    private static readonly System.Buffers.SearchValues<char> PathSeparators =
        System.Buffers.SearchValues.Create(@"\/");

    private static async Task AssertValidAsync(string yaml, string generatorName)
    {
        var diagnostics = await ManifestLoader.ValidateAsync(yaml, generatorName);
        Errors(diagnostics).Should().BeEmpty(
            "{0} must only ever produce a schema-valid manifest. YAML was:\n{1}",
            generatorName,
            yaml);
    }

    private static string[] Errors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Code}: {d.Message}"))
            .ToArray();

    private static IEnumerable<InstallStep> AllSteps(SigilManifest manifest) =>
        (manifest.PreInstall ?? Array.Empty<InstallStep>())
            .Concat(manifest.InstallSteps ?? Array.Empty<InstallStep>())
            .Concat(manifest.PostInstall ?? Array.Empty<InstallStep>())
            .Concat(manifest.Uninstall ?? Array.Empty<InstallStep>());
}
