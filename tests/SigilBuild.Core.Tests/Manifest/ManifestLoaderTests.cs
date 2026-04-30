using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class ManifestLoaderTests
{
    private sealed class MapEnv : IEnvironmentReader
    {
        private readonly Dictionary<string, string> _data;
        public MapEnv(Dictionary<string, string> data) { _data = data; }
        public string? Get(string name) => _data.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public async Task LoadAsync_ValidFile_ReturnsManifestNoDiagnostics()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");
        await File.WriteAllTextAsync(path, """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: 0.1.0, publisher: Example Inc. }
            build: { source: ./out }
            """);
        try
        {
            var result = await ManifestLoader.LoadAsync(path, new MapEnv(new()));
            result.Manifest.Should().NotBeNull();
            result.Diagnostics.Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsFileNotFoundDiagnostic()
    {
        var result = await ManifestLoader.LoadAsync("/does/not/exist.yaml", new MapEnv(new()));
        result.Manifest.Should().BeNull();
        result.Diagnostics.Should().ContainSingle()
            .Which.Code.Should().Be(DiagnosticCodes.FileNotFound);
    }
}
