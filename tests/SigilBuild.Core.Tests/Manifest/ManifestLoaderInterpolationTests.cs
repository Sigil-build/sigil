using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class ManifestLoaderInterpolationTests
{
    private sealed class MapEnv : IEnvironmentReader
    {
        private readonly Dictionary<string, string> _data;
        public MapEnv(Dictionary<string, string> data) { _data = data; }
        public string? Get(string name) => _data.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public async Task LoadAsync_ExpandsKnownEnvVariable()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");
        await File.WriteAllTextAsync(path, """
            spec: v1.0
            app: { id: ${APP_ID}, name: Example, version: 0.1.0, publisher: P }
            build: { source: ./out }
            """);
        try
        {
            var env = new MapEnv(new() { ["APP_ID"] = "com.example.Sub" });
            var result = await ManifestLoader.LoadAsync(path, env);

            result.Manifest.Should().NotBeNull();
            result.Manifest!.App.Id.Should().Be("com.example.Sub");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_MissingEnvVariable_ReportsAndFails()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");
        await File.WriteAllTextAsync(path, """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: 0.1.0, publisher: P }
            build: { source: ${MISSING_DIR} }
            """);
        try
        {
            var result = await ManifestLoader.LoadAsync(path, new MapEnv(new()));

            result.Manifest.Should().BeNull();
            result.Diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.EnvVariableMissing);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_SchemaError_ReturnsNullManifest()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");
        await File.WriteAllTextAsync(path, """
            spec: v1.0
            app: { id: bad, name: Example, version: 0.1.0, publisher: P }
            build: { source: ./out }
            """);
        try
        {
            var result = await ManifestLoader.LoadAsync(path, new MapEnv(new()));

            result.Manifest.Should().BeNull();
            result.Diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.SchemaViolation);
        }
        finally { File.Delete(path); }
    }
}
