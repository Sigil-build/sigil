using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Localization;

/// <summary>
/// Task 16 (P9): end-to-end fixtures that exercise the localization mechanism
/// (Tasks 1-15) through the real manifest -&gt; resolve -&gt; render path, rather
/// than the hand-built <see cref="InstallerScreen"/> lists
/// <see cref="ViewModelLocalizationTests"/> uses.
/// </summary>
/// <remarks>
/// <para><b>Layer exercised:</b> this is a VM-level render test, NOT a spawned
/// packed-exe test. It drives the real <see cref="ManifestLoader"/> (the same
/// parser <c>sigil pack</c> uses) against the fixture YAML on disk, the real
/// <see cref="LanguageResolver"/> (the same resolver
/// <c>InstallSession.ResolveSessionLanguage</c> calls at session start), and the
/// real <see cref="InstallerViewModel"/> — but it stops short of
/// <see cref="SigilBuild.Packaging.ExeWrapper.ExeWrapperPackager"/> and a spawned
/// setup.exe, because packing an EXE wrapper requires the Native-AOT-published
/// <c>SigilBuild.Installer.Host</c> runtime staged under
/// <c>runtimes/win-x64/</c> (<c>scripts/publish-installer-runtime.ps1</c>), which
/// in turn requires the MSVC C++ Native AOT linker. That toolchain is absent on
/// this dev box (link.exe), so a genuinely spawned-exe leg lives in
/// <c>SigilBuild.Wrapper.IntegrationTests.LocalizationEndToEndTests</c> instead,
/// gated exactly like the existing T13 VM-style tests
/// (<c>SIGIL_VM_TESTS=1</c> + a staged runtime) — see that class's remarks.</para>
/// <para><b>Fixtures</b> live at
/// <c>tests/SigilBuild.Packaging.IntegrationTests/Fixtures/localized-{uk,de}/sigil.yaml</c>
/// (shared with the packed-exe leg) rather than under this project, so both
/// layers exercise the exact same manifest text.</para>
/// </remarks>
[Collection("SessionLanguage")]
public sealed class LocalizationEndToEndTests : IDisposable
{
    public void Dispose() => SessionLanguage.SetForTesting(Lang.En);

    private static string FindRepoFile(string repoRelativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("Sigil.slnx").Length == 0)
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate repo root (Sigil.slnx) above " + AppContext.BaseDirectory);
        }
        return Path.Combine(dir.FullName, repoRelativePath);
    }

    private static async Task<SigilManifest> LoadFixtureManifestAsync(string fixtureName)
    {
        var path = FindRepoFile(Path.Combine(
            "tests", "SigilBuild.Packaging.IntegrationTests", "Fixtures", fixtureName, "sigil.yaml"));
        var result = await ManifestLoader.LoadAsync(path, new ProcessEnvironmentReader());
        result.Manifest.Should().NotBeNull(
            "fixture '{0}' must be schema-valid; diagnostics: {1}",
            fixtureName,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result.Manifest!;
    }

    private static List<ParameterDefinition> ParametersOf(SigilManifest manifest) =>
        manifest.Parameters?.Values.ToList() ?? new List<ParameterDefinition>();

    /// <summary>
    /// The uk fixture, headed: Sigil ships a uk chrome catalog (Task 12) AND the
    /// manifest's own <c>configure</c> screen declares a uk title, so under
    /// <c>/lang=uk</c> BOTH the declared screen and the chrome resolve to
    /// Ukrainian — the "everything lines up" case that contrasts with the de
    /// fixture below (design §4.4).
    /// </summary>
    [Fact]
    public async Task UkFixture_RendersUkrainianChromeAndDeclaredScreens()
    {
        var manifest = await LoadFixtureManifestAsync("localized-uk");

        var preferences = LanguageResolver.Preferences(
            manifestLanguage: manifest.Installer?.Language, langFlag: "uk", osPreferences: Array.Empty<string>());
        var chrome = LanguageResolver.MatchChrome(preferences);
        chrome.Should().Be(Lang.Uk, "the uk chrome catalog exists and /lang=uk was given");
        SessionLanguage.SetForTesting(chrome);

        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadScreens(manifest.Installer!.Screens!, ParametersOf(manifest), preferences);

        // Declared screen text, from the manifest's own uk map.
        vm.RailSteps.Should().Contain(s => s.Label == "Налаштування");
        vm.RailSteps.Should().NotContain(s => s.Label == "Configure");

        // Chrome, from the shipped uk catalog. There is no NextLabel on the VM —
        // the button caption is XAML {x:Static} — so assert the resolved chrome
        // string directly, the same value the button would bind to.
        SessionLanguage.Current.Should().Be(Lang.Uk);
        Strings.NavNext(SessionLanguage.Current).Should().Be("Далі");
    }

    /// <summary>
    /// The asymmetry fixture (design §4.4's crux). Sigil ships NO <c>de</c> chrome
    /// catalog (Task 12 only shipped en + uk), so <see cref="LanguageResolver.MatchChrome"/>
    /// falls back to <see cref="Lang.En"/> — but the manifest's own <c>configure</c>
    /// screen supplies a <c>de</c> title, and that resolves against the SAME
    /// preference list independently of the chrome fallback. If chrome and
    /// manifest text were (incorrectly) coupled — e.g. the declared screen matched
    /// against the resolved CHROME language instead of the session's full
    /// preference list — this screen could only ever show "Configure" (English),
    /// never "Konfigurieren", because <see cref="Lang"/> has no <c>De</c> member
    /// for it to fall back through. This test fixes exactly that gap (see
    /// <c>InstallerViewModel.LoadScreens</c>'s new <c>languagePreferences</c>
    /// parameter and <c>App.axaml.cs</c>'s wiring of <c>session.LanguagePreferences</c>).
    /// </summary>
    [Fact]
    public async Task DeFixture_RendersGermanScreens_WithEnglishChrome()
    {
        var manifest = await LoadFixtureManifestAsync("localized-de");

        var preferences = LanguageResolver.Preferences(
            manifestLanguage: manifest.Installer?.Language, langFlag: "de", osPreferences: Array.Empty<string>());
        var chrome = LanguageResolver.MatchChrome(preferences);
        chrome.Should().Be(Lang.En, "Sigil ships no de chrome; MatchChrome falls back to en");
        SessionLanguage.SetForTesting(chrome);

        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadScreens(manifest.Installer!.Screens!, ParametersOf(manifest), preferences);

        // The manifest supplies de — must render German, NOT the English fallback
        // and NOT some other language entirely (specific, not "any non-English").
        vm.RailSteps.Should().Contain(s => s.Label == "Konfigurieren", "the manifest supplies de");
        vm.RailSteps.Should().NotContain(s => s.Label == "Configure");

        // The chrome stays English — specifically "Next", not a Ukrainian or
        // German string — because Sigil ships no de catalog at all.
        SessionLanguage.Current.Should().Be(Lang.En, "Sigil ships no de chrome; it falls back to en");
        Strings.NavNext(SessionLanguage.Current).Should().Be("Next");
    }

    /// <summary>
    /// Validates the <c>localized-uk-fixed</c> fixture (localized-uk plus a fixed
    /// <c>installer.language: en</c>) at the manifest + resolver layer — the parts
    /// reachable WITHOUT a spawned exe. The full behavior this fixture backs
    /// (silent run exits 0, the log records "manifest pin 'en' overrides
    /// /lang=uk") is asserted end-to-end, against a real packed + spawned
    /// setup.exe, by
    /// <c>SigilBuild.Wrapper.IntegrationTests.LocalizationEndToEndTests.FixedManifestLanguage_LogsAndIgnoresLangFlag</c> —
    /// soft-skipped on this box (no staged AOT runtime). This test at least
    /// proves the fixture YAML parses and that <see cref="LanguageResolver"/>
    /// resolves the pin correctly, so that soft-skipped leg isn't the only
    /// thing standing between this fixture and a schema/logic mistake.
    /// </summary>
    [Fact]
    public async Task UkFixedFixture_ManifestPinWins_OverLangFlag()
    {
        var manifest = await LoadFixtureManifestAsync("localized-uk-fixed");
        manifest.Installer?.Language.Should().Be("en");

        var preferences = LanguageResolver.Preferences(
            manifestLanguage: manifest.Installer?.Language, langFlag: "uk", osPreferences: Array.Empty<string>());
        preferences.Should().Equal("en");
        LanguageResolver.MatchChrome(preferences).Should().Be(Lang.En);
    }
}
