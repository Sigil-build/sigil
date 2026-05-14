using System.Text.Json;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views.Screens;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Screens;

public class InstallOptionsViewTests
{
    [AvaloniaFact]
    public void Default_InstallPath_PointsAtProgramFiles()
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" });
        // No install-time parameters declared -> ParameterFields is empty and
        // the wizard falls back to InstallPath seeded from Program Files\AppName.
        vm.InstallPath.Should().Contain("Example");
    }

    [AvaloniaFact]
    public void InstallOptionsView_Loads_Without_Errors()
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" });
        // Loading the AXAML should succeed even when no parameters are declared
        // (ParameterFields is empty -> ItemsControl simply renders nothing).
        var view = new InstallOptionsView { DataContext = vm };
        view.Should().NotBeNull();
    }

    [AvaloniaFact]
    public void EnumParameter_BuildsFieldVm_WithValues_And_IsEnum_True()
    {
        var parameters = new[]
        {
            new InstallTimeParameter
            {
                Name = "edition",
                Type = "enum",
                InstallTime = true,
                Description = "Edition",
                Values = new[] { "free", "pro", "enterprise" },
                Default = JsonDocument.Parse("\"pro\"").RootElement,
            },
        };
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" }, parameters);

        vm.ParameterFields.Should().HaveCount(1);
        var f = vm.ParameterFields[0];
        f.Name.Should().Be("edition");
        f.Label.Should().Be("Edition");
        f.Type.Should().Be("enum");
        f.IsEnum.Should().BeTrue();
        f.IsTextual.Should().BeFalse();
        f.Values.Should().BeEquivalentTo("free", "pro", "enterprise");
        f.CurrentValue.Should().Be("pro");
    }

    [AvaloniaFact]
    public void StringParameter_BuildsFieldVm_With_IsTextual_True_And_NoValues()
    {
        var parameters = new[]
        {
            new InstallTimeParameter
            {
                Name = "license_key",
                Type = "string",
                InstallTime = true,
                Description = "License key",
                Default = JsonDocument.Parse("\"\"").RootElement,
            },
        };
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" }, parameters);

        vm.ParameterFields.Should().HaveCount(1);
        var f = vm.ParameterFields[0];
        f.IsEnum.Should().BeFalse();
        f.IsTextual.Should().BeTrue();
        f.Values.Should().BeNull();
    }

    [AvaloniaFact]
    public void CurrentValue_Edit_FlowsBack_Into_ParameterValues()
    {
        var parameters = new[]
        {
            new InstallTimeParameter
            {
                Name = "edition",
                Type = "enum",
                InstallTime = true,
                Values = new[] { "free", "pro" },
                Default = JsonDocument.Parse("\"free\"").RootElement,
            },
        };
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" }, parameters);

        vm.ParameterValues["edition"].Should().Be("free");
        vm.ParameterFields[0].CurrentValue = "pro";
        vm.ParameterValues["edition"].Should().Be("pro");
    }

    [AvaloniaFact]
    public void Editing_InstallDir_Field_Mirrors_Into_InstallPath()
    {
        var parameters = new[]
        {
            new InstallTimeParameter
            {
                Name = "install_dir",
                Type = "path",
                InstallTime = true,
                Default = JsonDocument.Parse("\"C:/Program Files/Example\"").RootElement,
            },
        };
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" }, parameters);

        vm.InstallPath.Should().Be("C:/Program Files/Example");
        vm.ParameterFields[0].CurrentValue = "D:/Apps/Example";
        vm.InstallPath.Should().Be("D:/Apps/Example");
        vm.ParameterValues["install_dir"].Should().Be("D:/Apps/Example");
    }
}
