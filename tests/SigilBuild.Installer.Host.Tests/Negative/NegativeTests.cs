using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Packaging.Installer;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Negative;

public class NegativeTests
{
    // ── BrandTokenEmitter negative tests (automated — no UI needed) ─────────

    [Fact]
    public void BrandTokenEmitter_LowContrast_ProducesWarning()
    {
        var manifest = BuildManifest(primaryColor: "#FFEE00");
        var result = BrandTokenEmitter.EmitWithDiagnostics(manifest);
        result.Warnings.Should().ContainSingle(w => w.Contains("WCAG AA"));
    }

    [Fact]
    public void BrandTokenEmitter_LowContrast_BlocksWhenOverrideNotSet()
    {
        var manifest = BuildManifest(primaryColor: "#FFEE00");
        var act = () => BrandTokenEmitter.Emit(manifest, allowLowContrast: false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*WCAG AA*--allow-low-contrast*");
    }

    [Fact]
    public void BrandTokenEmitter_GoodContrast_ProducesNoWarnings()
    {
        var manifest = BuildManifest(primaryColor: "#1F2937");
        var result = BrandTokenEmitter.EmitWithDiagnostics(manifest);
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void BrandTokenEmitter_NullBrand_UsesSafeDefaults()
    {
        var manifest = BuildManifest(primaryColor: null);
        var json = BrandTokenEmitter.Emit(manifest);
        json.Should().Contain("#1F2937");
    }

    // ── InstallerViewModel navigation guard tests ────────────────────────────

    [Fact]
    public void InstallerViewModel_LicenseNotAccepted_BlocksNextFromLicense()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.CurrentStep = InstallerStep.License;
        vm.LicenseAccepted = false;

        vm.Next();

        vm.CurrentStep.Should().Be(InstallerStep.License);
    }

    [Fact]
    public void InstallerViewModel_LicenseAccepted_AllowsNextFromLicense()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.CurrentStep = InstallerStep.License;
        vm.LicenseAccepted = true;

        vm.Next();

        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions);
    }

    // ── BrandTokens fallback test ────────────────────────────────────────────

    [Fact]
    public void BrandTokens_MissingFile_FallsBackToDefaults()
    {
        var tokens = BrandTokens.LoadOrDefault("nonexistent_brand_tokens_xyz.json");
        tokens.AppName.Should().Be("Application");
        tokens.PrimaryColor.Should().Be("#1F2937");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SigilManifest BuildManifest(string? primaryColor) =>
        new("v1.0",
            new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
            new BuildSection("./out", null, null, true),
            null, null, null, null,
            Installer: primaryColor is null ? null : new InstallerSection(
                new InstallerBrand(null, null, primaryColor, "#3B82F6", null, null, null),
                null),
            Location: SourceLocation.Unknown);
}
