using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;
using SigilBuild.Wrapper.Core.Localization;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Localization;

/// <summary>
/// Task 15 (P9): the runtime half of the "zero hardcoded strings" guarantee.
/// Renders every wizard screen under <see cref="Lang.Pseudo"/> — the generator's
/// bracket-transform test language (see <c>PseudoTransform</c>: every catalog
/// value comes back wrapped as <c>[...‼]</c>) — walks the real visual tree, and
/// asserts every rendered <see cref="TextBlock"/> string is bracketed. A
/// plain-ASCII run on screen means it never went through the S/Strings catalog.
///
/// This mutates the process-wide <see cref="SessionLanguage"/> (same reason as
/// <c>ViewModelLocalizationTests</c>), so it is serialized in the shared
/// "SessionLanguage" collection and restores the assembly's English default in
/// <see cref="Dispose"/>.
///
/// <see cref="InstallerStep.Failed"/>, <see cref="InstallerStep.CloseApps"/> and
/// <see cref="InstallerStep.DowngradeBlocked"/> cannot be reached by setting
/// <c>CurrentStep</c> alone — each needs real VM state — so they get their own
/// test, driven the same way <c>InstallFlowTests</c>, <c>CloseAppsGateTests</c>
/// and <c>UpgradeNoticeTests</c> already do. <see cref="InstallerStep.Custom"/> is
/// not covered here: it requires a manifest-declared screen instance, which no
/// existing host test constructs standalone either.
/// </summary>
[Collection("SessionLanguage")]
public sealed class PseudoLocRenderTests : IDisposable
{
    // T14: real license text is manifest/user-authored content that is loaded
    // verbatim (InstallerLicenseLoader), never routed through the catalog — the
    // "English step detail" exception design D2 / §8.1 calls out. This fixture
    // stands in for it so the License screen's OWN text doesn't need bracketing
    // to prove the chrome around it (title, accept checkbox, rail, buttons) does.
    private const string LicenseFixtureText = "Example EULA text.";

    // Design D2 (docs/plan/feature-parity/P9-DESIGN-localization.md): the catalog
    // covers prose engine messages only — per-step failure detail stays English by
    // design. This is the exact shape InstallFlowTests drives the Failed screen
    // with (an InstallOutcome.Error string), reproduced here for the same reason.
    private const string EngineFailureFixtureText = "install_steps: access denied";

    // P6: blocker descriptions are live process name/PID text from the Restart
    // Manager scan (CloseAppsGateTests' fixture shape) — user-machine data, not a
    // catalog string.
    private const string BlockerFixtureText = "Acme Studio (pid 42)";

    // Restore the assembly-wide English default (TestAppBuilder's ModuleInitializer)
    // rather than nulling it out, so whichever test class the runner orders next
    // never observes this class's Lang.Pseudo.
    public void Dispose() => SessionLanguage.SetForTesting(Lang.En);

    [AvaloniaTheory]
    [InlineData(InstallerStep.Welcome)]
    [InlineData(InstallerStep.License)]
    [InlineData(InstallerStep.InstallOptions)]
    [InlineData(InstallerStep.Options)]
    [InlineData(InstallerStep.Installing)]
    [InlineData(InstallerStep.Finish)]
    public void EveryRenderedString_IsPseudoLocalized(InstallerStep step)
    {
        SessionLanguage.SetForTesting(Lang.Pseudo);

        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadLicense(LicenseFixtureText); // keeps the rail/flow shape identical across cases
        vm.CurrentStep = step;

        AssertWindowIsFullyPseudoLocalized(vm);
    }

    [AvaloniaFact]
    public async Task FailedScreen_IsPseudoLocalized()
    {
        SessionLanguage.SetForTesting(Lang.Pseudo);

        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadLicense(LicenseFixtureText);
        vm.ConfigureInstallRunner((progress, ct) =>
            Task.FromResult(new InstallOutcome(false, EngineFailureFixtureText)));

        vm.Next(); // Welcome -> InstallOptions
        vm.Next(); // InstallOptions -> License
        vm.LicenseAccepted = true;
        vm.Next(); // License -> Installing (fires the fake engine)

        await vm.InstallTask!;
        vm.CurrentStep.Should().Be(InstallerStep.Failed, "the fake runner reports a step failure");

        AssertWindowIsFullyPseudoLocalized(vm);
    }

    [AvaloniaFact]
    public void CloseAppsScreen_IsPseudoLocalized()
    {
        SessionLanguage.SetForTesting(Lang.Pseudo);

        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadLicense(LicenseFixtureText);
        vm.ConfigureBlockerProbe(scan: _ => new[] { BlockerFixtureText }, close: _ => { });

        vm.Next(); // Welcome -> InstallOptions
        vm.Next(); // InstallOptions -> License
        vm.LicenseAccepted = true;
        vm.Next(); // License -> would-be Installing, diverted by the P6 blocker gate

        vm.CurrentStep.Should().Be(InstallerStep.CloseApps, "an active blocker diverts the flow here");

        AssertWindowIsFullyPseudoLocalized(vm);
    }

    [AvaloniaFact]
    public void DowngradeBlockedScreen_IsPseudoLocalized()
    {
        SessionLanguage.SetForTesting(Lang.Pseudo);

        var vm = new InstallerViewModel(new BrandTokens());
        vm.SetUpgradeState(UpgradeAction.DowngradeBlocked, "2.0.0");

        vm.CurrentStep.Should().Be(InstallerStep.DowngradeBlocked);

        AssertWindowIsFullyPseudoLocalized(vm);
    }

    private static void AssertWindowIsFullyPseudoLocalized(InstallerViewModel vm)
    {
        var window = new InstallerWindow { DataContext = vm };
        window.Show();

        var texts = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Where(t => !IsAllowed(t!))
            .ToArray();

        texts.Should().OnlyContain(t => t!.StartsWith('[') && t.EndsWith(']'),
            "a plain-ASCII string on screen means it never went through the catalog");
    }

    // Glyphs, brand/version data, user-entered field values and per-step engine
    // failure detail are legitimately un-pseudo — they are not catalog strings
    // (design D2, §8.1). An exact allowlist (not a broad predicate) so a
    // genuinely hardcoded catalog-shaped string still fails loudly.
    private static bool IsAllowed(string text) =>
        text is "🔒" or "✓" or "•" or "••••••••"
        || text == LicenseFixtureText
        || text == EngineFailureFixtureText
        || text == BlockerFixtureText
        || text.StartsWith("1.0.0", StringComparison.Ordinal)
        // The Installing screen's Avalonia FluentTheme ProgressBar renders its own
        // built-in percentage overlay (e.g. "0%") from Avalonia.Controls/Avalonia.
        // Themes.Fluent — confirmed by grepping the repo's own source for the
        // literal, which only turns up inside those two Avalonia DLLs. It is
        // framework control chrome our S/Strings catalog never touches (Tasks
        // 12-14 migrated only SigilBuild.Installer.Host's own XAML/VM strings),
        // so a bare "NN%" is allowed — this regex cannot match any real prose.
        || System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{1,3}%$");
}
