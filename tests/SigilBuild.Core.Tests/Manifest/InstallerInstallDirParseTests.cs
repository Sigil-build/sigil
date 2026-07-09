using FluentAssertions;
using SigilBuild.Core.Configuration;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// T13: parsing of <c>installer.install_dir</c> into
/// <see cref="SigilBuild.Core.Manifest.InstallerSection.InstallDir"/>. The value
/// is captured verbatim as a template; token resolution happens at install time.
/// </summary>
public class InstallerInstallDirParseTests
{
    private static string Yaml(string installDirLine) => $$"""
        spec: v1.0
        app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }
        build: { source: ./out }
        installer:
          {{installDirLine}}
          brand:
            primaryColor: "#312E81"
            accentColor: "#4F46E5"
        """;

    [Fact]
    public void Parses_install_dir_override_verbatim()
    {
        var result = ManifestParser.Parse(Yaml("install_dir: \"{scope_root}/Acme Studio\""), "s.yaml");
        result.Diagnostics.Should().BeEmpty();
        result.Manifest!.Installer!.InstallDir.Should().Be("{scope_root}/Acme Studio");
    }

    [Fact]
    public void Absent_install_dir_is_null()
    {
        var result = ManifestParser.Parse(Yaml("# no install_dir"), "s.yaml");
        result.Manifest!.Installer!.InstallDir.Should().BeNull();
    }

    [Fact]
    public void Blank_install_dir_is_treated_as_absent()
    {
        var result = ManifestParser.Parse(Yaml("install_dir: \"   \""), "s.yaml");
        result.Manifest!.Installer!.InstallDir.Should().BeNull();
    }
}
