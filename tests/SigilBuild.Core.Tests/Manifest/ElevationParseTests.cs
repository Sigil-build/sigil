// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// <c>installer.elevation</c>: <c>on_demand</c> (default) or <c>on_start</c>; an
/// unknown value is the warning SIG0261, and <c>on_start</c> with <c>scope: user</c>
/// is the error SIG0262. (R118)
/// </summary>
public class ElevationParseTests
{
    private static ParseResult Parse(string installerBody) => ManifestParser.Parse(
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n" +
        "installer:\n" + installerBody,
        "sigil.yaml");

    [Fact]
    public void An_absent_elevation_is_on_demand()
    {
        // Act
        var result = Parse("  scope: auto\n");

        // Assert
        result.Manifest!.Installer!.Elevation.Should().Be(InstallerElevation.OnDemand);
        result.Diagnostics.Should().NotContain(d => d.Code == "SIG0261" || d.Code == "SIG0262");
    }

    [Theory]
    [InlineData("auto", "on_demand", InstallerElevation.OnDemand)]
    [InlineData("auto", "on_start", InstallerElevation.OnStart)]
    [InlineData("machine", "on_start", InstallerElevation.OnStart)]
    [InlineData("user", "on_demand", InstallerElevation.OnDemand)]
    [InlineData("auto", "ON_START", InstallerElevation.OnStart)]
    public void A_declared_value_parses(string scope, string value, InstallerElevation expected)
    {
        // Act
        var result = Parse($"  scope: {scope}\n  elevation: {value}\n");

        // Assert
        result.Manifest!.Installer!.Elevation.Should().Be(expected);
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void On_start_with_a_per_user_scope_is_refused()
    {
        // Act
        var result = Parse("  scope: user\n  elevation: on_start\n");

        // Assert
        result.Diagnostics.Should().Contain(
            d => d.Severity == DiagnosticSeverity.Error && d.Code == DiagnosticCodes.ElevationOnStartWithUserScope,
            "an elevated per-user install lands in the profile of whoever approved the prompt");
    }

    [Fact]
    public void An_unknown_value_warns_and_falls_back_to_on_demand()
    {
        // Act
        var result = Parse("  scope: auto\n  elevation: always\n");

        // Assert
        result.Diagnostics.Should().Contain(
            d => d.Severity == DiagnosticSeverity.Warning && d.Code == DiagnosticCodes.InvalidInstallerElevation);
        result.Manifest!.Installer!.Elevation.Should().Be(InstallerElevation.OnDemand);
    }
}
