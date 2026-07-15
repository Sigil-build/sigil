using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// P6 (gap G7): parsing of <c>installer.app_mutex</c> — the Inno AppMutex equivalent.
/// </summary>
public class AppMutexParseTests
{
    private const string Header =
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n";

    [Fact]
    public void Parses_the_declared_mutex_names_in_order()
    {
        var result = ManifestParser.Parse(
            Header + "installer:\n  app_mutex: [\"Global\\\\AcmeStudio\", \"Local\\\\AcmeHelper\"]\n", "s.yaml");

        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        result.Manifest!.Installer!.AppMutex.Should().Equal("Global\\AcmeStudio", "Local\\AcmeHelper");
    }

    [Fact]
    public void Absent_app_mutex_is_null()
        => ManifestParser.Parse(Header + "installer:\n  scope: user\n", "s.yaml")
            .Manifest!.Installer!.AppMutex.Should().BeNull();
}
