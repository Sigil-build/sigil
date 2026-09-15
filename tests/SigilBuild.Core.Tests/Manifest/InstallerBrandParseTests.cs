// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// <c>installer.brand</c>'s colours reach the typed graph (R80).
/// </summary>
/// <remarks>
/// Nothing asserted this before, and the gap was wide enough that the repo's own
/// reference fixture — the one whose job is to exercise the whole installer
/// surface — spelled the keys <c>primary_color</c> / <c>accent_color</c>, which the
/// schema accepted and the parser read as nothing. It validated, packed, and
/// produced an unbranded installer. The schema-only test that covered it could not
/// have caught that, because a document can validate and still mean nothing: only
/// driving it through the parser and asserting on the result can.
/// </remarks>
public class InstallerBrandParseTests
{
    private const string Header =
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n";

    [Fact]
    public void Brand_colours_reach_the_typed_graph()
    {
        // Arrange
        var yaml = Header +
            "installer:\n" +
            "  brand:\n" +
            "    primaryColor: \"#312E81\"\n" +
            "    accentColor: \"#4F46E5\"\n";

        // Act
        var result = ManifestParser.Parse(yaml, "s.yaml");

        // Assert
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        var brand = result.Manifest!.Installer!.Brand!;
        brand.PrimaryColor.Should().Be("#312E81");
        brand.AccentColor.Should().Be("#4F46E5");
    }

    [Fact]
    public void Snake_case_colour_keys_do_not_reach_the_typed_graph()
    {
        // Arrange — the spelling R80 retired. The schema refuses it now
        // (additionalProperties: false, covered by a Schema.Tests fixture); this
        // pins the parser half, which is what made the old spelling dangerous: it
        // produced a brand object with null colours and no diagnostic, so the
        // palette silently fell back to Sigil's own.
        var yaml = Header +
            "installer:\n" +
            "  brand:\n" +
            "    primary_color: \"#312E81\"\n" +
            "    accent_color: \"#4F46E5\"\n";

        // Act
        var result = ManifestParser.Parse(yaml, "s.yaml");

        // Assert
        result.Manifest!.Installer!.Brand!.PrimaryColor.Should().BeNull(
            "the parser has only ever read camelCase — which is precisely why the "
            + "schema must not advertise the other spelling as valid");
        result.Manifest.Installer.Brand.AccentColor.Should().BeNull();
    }

    [Fact]
    public void An_absent_brand_block_leaves_the_colours_unset()
    {
        // Arrange
        var yaml = Header + "installer:\n  scope: user\n";

        // Act
        var result = ManifestParser.Parse(yaml, "s.yaml");

        // Assert
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        result.Manifest!.Installer!.Brand.Should().BeNull(
            "no brand block means the emitter's defaults apply, which is a supported shape");
    }
}
