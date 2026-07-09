using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// T14 parse coverage: the manifest's <c>installer.license</c> field is captured
/// into <see cref="SigilBuild.Core.Manifest.InstallerSection.License"/> as a raw
/// path string. The file read + embed happens later, at pack time.
/// </summary>
public class LicenseSchemaTests
{
    private const string Prelude = """
        spec: v1.0
        app:
          id: com.acme.Studio
          name: Acme Studio
          version: 3.2.0
          publisher: Acme, Inc.
        build:
          source: ./out
        """;

    [Fact]
    public void License_path_is_captured_onto_installer_section()
    {
        var yaml = Prelude + """

            installer:
              license: ./LICENSE.txt
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        result.Manifest!.Installer!.License.Should().Be("./LICENSE.txt");
    }

    [Fact]
    public void Omitted_license_leaves_it_null()
    {
        var yaml = Prelude + """

            installer:
              brand:
                primaryColor: "#312E81"
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Manifest!.Installer!.License.Should().BeNull();
    }
}
