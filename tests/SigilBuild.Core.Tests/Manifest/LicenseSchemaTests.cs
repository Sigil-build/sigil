using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// T14 / P9 (gap G10) parse coverage: the manifest's <c>installer.license</c>
/// field is captured into <see cref="SigilBuild.Core.Manifest.InstallerSection.License"/>
/// as a <see cref="SigilBuild.Core.Manifest.LocalizedText"/> — a plain string
/// (path) or a <c>{en: ..., uk: ...}</c> map of per-language paths, through the
/// same <c>ParseLocalizedText</c> path as title/subtitle/description. The file
/// read + embed happens later, at pack time.
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
        result.Manifest!.Installer!.License!.Values["en"].Should().Be("./LICENSE.txt");
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

    /// <summary>
    /// The regression this task exists to prevent: before this task,
    /// <c>InstallerSection.License</c> was <c>string?</c> and parsed via
    /// <c>GetScalar</c>, which returns <c>null</c> with zero diagnostic for any
    /// non-scalar node. A manifest declaring a per-language license map passed
    /// schema validation, silently produced <c>License = null</c>, and the
    /// License screen vanished with no error or warning. It must now parse into
    /// a map that carries every declared language.
    /// </summary>
    [Fact]
    public void Map_license_no_longer_silently_vanishes()
    {
        var yaml = Prelude + """

            installer:
              license:
                en: LICENSE.txt
                uk: LICENSE.uk.txt
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        result.Manifest!.Installer!.License.Should().NotBeNull();
        result.Manifest!.Installer!.License!.Values["en"].Should().Be("LICENSE.txt");
        result.Manifest!.Installer!.License!.Values["uk"].Should().Be("LICENSE.uk.txt");
    }

    [Fact]
    public void Map_license_missing_english_emits_Sig0290()
    {
        var yaml = Prelude + """

            installer:
              license:
                uk: LICENSE.uk.txt
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.LocalizedTextMissingEnglish && d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// The other silent-drop shape fixed alongside the license bug: a per-language
    /// value that isn't a plain scalar (here, a nested sequence under `en:`) used
    /// to collapse to "" with no diagnostic.
    /// </summary>
    [Fact]
    public void Map_license_with_non_scalar_value_emits_Sig0292()
    {
        var yaml = Prelude + """

            installer:
              license:
                en:
                  - LICENSE.txt
                uk: LICENSE.uk.txt
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.LocalizedTextValueNotScalar && d.Severity == DiagnosticSeverity.Error);
    }
}
