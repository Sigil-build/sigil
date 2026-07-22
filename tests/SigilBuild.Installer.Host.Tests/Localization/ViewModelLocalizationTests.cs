using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Localization;

/// <summary>
/// Task 13 (P9): ViewModel + code-behind chrome routed through the catalog. These
/// mutate the process-wide <see cref="SessionLanguage"/>, so they're serialized in
/// their own collection and reset it in <see cref="Dispose"/> — this assembly's
/// other tests (e.g. bare <c>new BrandTokens()</c> expecting the English fallback)
/// must never observe a language left set by a prior test.
/// </summary>
[Collection("SessionLanguage")]
public sealed class ViewModelLocalizationTests : IDisposable
{
    // Restore the assembly-wide English default (set once by TestAppBuilder's
    // ModuleInitializer) rather than nulling it out — a bare ResetForTesting()
    // here would leave SessionLanguage unset for whichever test class the
    // runner happens to order next, reintroducing the Debug-mode throw this
    // class's own SetForTesting calls avoid for themselves.
    public void Dispose() => SessionLanguage.SetForTesting(Lang.En);

    [Fact]
    public void InstallPathError_IsLocalized()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var vm = new InstallerViewModel(new BrandTokens()) { InstallPath = string.Empty };

        vm.ValidateDestination().Should().BeFalse();

        vm.InstallPathError.Should().Be("Укажіть розташування для встановлення.");
    }

    [Fact]
    public void RailLabels_NeverLeakEnumNamesOrScreenIds()
    {
        SessionLanguage.SetForTesting(Lang.En);
        var vm = new InstallerViewModel(new BrandTokens());

        vm.RailSteps.Should().NotContain(s => s.Label == nameof(InstallerStep.Finish));
        vm.RailSteps.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Label));
    }

    [Fact]
    public void DowngradeNotice_IsOneKey_NotThreeFragments()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var text = Strings.DowngradeBody(Lang.Uk, "2.1.0", "Acme", "1.0.0");

        text.Should().StartWith("Уже встановлено новішу версію");
        text.Should().Contain("Acme");
    }

    /// <summary>
    /// Critical 1 fix-round-1 regression: the pre-Avalonia single-instance
    /// MessageBoxW in <c>Program.cs</c> must resolve its body/caption through the
    /// catalog (<c>already_running.body</c> / <c>already_running.caption</c>) rather
    /// than a hardcoded English literal. A Win32 MessageBoxW can't be driven from a
    /// unit test, so this asserts the two things that ARE testable: the catalog
    /// entries are real, distinct-per-language strings, and the source no longer
    /// contains the old hardcoded literal but does call through <see cref="Strings"/>.
    /// </summary>
    [Fact]
    public void AlreadyRunning_CatalogStrings_AreLocalizedPerLanguage()
    {
        var en = Strings.AlreadyRunningBody(Lang.En);
        var uk = Strings.AlreadyRunningBody(Lang.Uk);
        var enCaption = Strings.AlreadyRunningCaption(Lang.En);
        var ukCaption = Strings.AlreadyRunningCaption(Lang.Uk);

        en.Should().NotBeNullOrWhiteSpace();
        uk.Should().NotBeNullOrWhiteSpace();
        uk.Should().NotBe(en, "the Ukrainian catalog entry must actually differ from English");
        ukCaption.Should().NotBe(enCaption);

        // The \n escape support (fix(localization) commit) must still render a real
        // paragraph break, not the literal two characters '\' 'n'.
        en.Should().Contain("\n\n");
    }

    [Fact]
    public void Program_HeadedAlreadyRunningPath_UsesTheCatalogNotAHardcodedLiteral()
    {
        var programCsPath = FindRepoFile(Path.Combine("src", "SigilBuild.Installer.Host", "Program.cs"));
        var source = File.ReadAllText(programCsPath);

        source.Should().NotContain(
            "Setup is already running.",
            "the headed MessageBox must no longer carry the hardcoded English literal");
        source.Should().Contain("Strings.AlreadyRunningBody(SessionLanguage.Current)");
        source.Should().Contain("Strings.AlreadyRunningCaption(SessionLanguage.Current)");
    }

    /// <summary>Walks up from the test output directory to the repo root (identified
    /// by <c>Sigil.slnx</c>) and resolves a repo-relative path — robust to Debug vs
    /// Release output layout differences.</summary>
    private static string FindRepoFile(string repoRelativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("Sigil.slnx").Length == 0)
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (Sigil.slnx) above " + AppContext.BaseDirectory);
        }

        return Path.Combine(dir.FullName, repoRelativePath);
    }

    [Fact]
    public void RailLabel_ForDeclaredScreen_PrefersManifestTitle_OverConfigureFallback()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var vm = new InstallerViewModel(new BrandTokens());

        var screens = new List<InstallerScreen>
        {
            new(
                "db_setup",
                new LocalizedText(new Dictionary<string, string>
                {
                    ["en"] = "Database Setup",
                    ["uk"] = "Налаштування бази даних",
                }),
                null,
                null,
                Array.Empty<ScreenField>()),
        };
        vm.LoadScreens(screens, Array.Empty<ParameterDefinition>());

        vm.RailSteps.Should().Contain(s => s.Label == "Налаштування бази даних");
        vm.RailSteps.Should().NotContain(s => s.Label == "db_setup");
        vm.RailSteps.Should().NotContain(s => s.Label == "Configure");
    }

    [Fact]
    public void RebootNotice_IsLocalized()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var vm = new InstallerViewModel(new BrandTokens());

        vm.SetRebootRequired(true);

        vm.RebootNotice.Should().Be(
            "Щоб завершити налаштування необхідного компонента, потрібно перезавантажити комп'ютер.");
    }

    [Fact]
    public void BrandFallbacks_AreLocalized()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var tokens = new BrandTokens();

        tokens.AppName.Should().Be("Програма");
        tokens.Publisher.Should().Be("Видавець");
    }
}
