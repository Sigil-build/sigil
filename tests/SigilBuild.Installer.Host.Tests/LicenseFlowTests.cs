using System.Linq;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// T14 host coverage: the License screen (and its rail entry) appear IFF the blob
/// carries license text; the embedded text is rendered; the "I accept" checkbox
/// gates Next. The headless <c>/silent</c> path never constructs the wizard, so
/// silent installs imply acceptance — modelled here by the license-absent flow,
/// which has no accept gate at all.
/// </summary>
public sealed class LicenseFlowTests
{
    private const string Eula = "ACME END USER LICENSE AGREEMENT\n\nYou may use this software.";

    // ── Screen + rail presence ───────────────────────────────────────────────

    [Fact]
    public void License_screen_and_rail_entry_present_when_license_loaded()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadLicense(Eula);

        vm.RailSteps.Select(r => r.Label).Should().Contain("License");

        vm.Next(); // Welcome → InstallOptions (destination)
        vm.Next(); // InstallOptions → License

        vm.CurrentStep.Should().Be(InstallerStep.License);
        vm.LicenseText.Should().Be(Eula);
    }

    [Fact]
    public void License_screen_and_rail_entry_absent_without_license()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        // No LoadLicense — mirrors an un-stamped host / a manifest with no license
        // and the headless path (which never shows the screen at all).

        vm.RailSteps.Select(r => r.Label).Should().NotContain("License");

        vm.Next(); // Welcome → InstallOptions (destination)
        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions);

        vm.Next(); // InstallOptions → Installing (no License in between)
        vm.CurrentStep.Should().Be(InstallerStep.Installing);
    }

    [Fact]
    public void Blank_license_text_is_treated_as_absent()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadLicense("   \n\t ");

        vm.RailSteps.Select(r => r.Label).Should().NotContain("License");
    }

    // ── Accept gates Next ─────────────────────────────────────────────────────

    [Fact]
    public void Accept_checkbox_gates_next()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadLicense(Eula);
        vm.Next(); // Welcome → InstallOptions
        vm.Next(); // InstallOptions → License
        vm.CurrentStep.Should().Be(InstallerStep.License);

        // Not accepted → Next is blocked, stays on License.
        vm.LicenseAccepted = false;
        vm.Next();
        vm.CurrentStep.Should().Be(InstallerStep.License);

        // Accepted → Next advances past the License screen.
        vm.LicenseAccepted = true;
        vm.Next();
        vm.CurrentStep.Should().NotBe(InstallerStep.License);
    }

    // ── Decision-4 ordering: destination before license ──────────────────────

    [Fact]
    public void License_sits_after_destination_in_the_rail()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadLicense(Eula);

        var labels = vm.RailSteps.Select(r => r.Label).ToList();
        labels.IndexOf("Location").Should().BeLessThan(labels.IndexOf("License"),
            "decision 4 orders welcome → destination → license");
        labels.IndexOf("Welcome").Should().BeLessThan(labels.IndexOf("Location"));
    }
}
