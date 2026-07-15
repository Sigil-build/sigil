using System.Text;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// P4: packing a manifest with an http_download step is deterministic and touches
/// no network — the URL is only fetched at install time. Two packs of the same
/// manifest produce byte-identical blob bytes.
/// </summary>
public class ExeWrapperHttpDownloadDeterminismTests
{
    private static SigilManifest ManifestWithDownload() =>
        new("v1.0",
            new AppSection("com.acme.Studio", "Acme Studio", "3.2.0", "Acme, Inc.", null, null),
            new BuildSection("./out", null, null, true),
            null, null, null, null,
            Installer: null,
            Location: SourceLocation.Unknown,
            InstallSteps: new InstallStep[]
            {
                new InstallStep.HttpDownload(
                    "dl", "https://ex.com/vc_redist.x64.exe", "{install_dir}/vc_redist.exe",
                    "aaaabbbbccccdddd", TimeoutSeconds: 120, Retries: 3, When: null, OnFailure: OnFailure.Rollback),
            });

    [Fact]
    public void BuildBlobBytes_is_deterministic_and_network_free_for_http_download()
    {
        var manifest = ManifestWithDownload();

        var first = ExeWrapperPackager.BuildBlobBytes(manifest, string.Empty);
        var second = ExeWrapperPackager.BuildBlobBytes(manifest, string.Empty);

        first.Should().Equal(second, "two packs of the same manifest must be byte-identical (no network at pack time)");

        var json = Encoding.UTF8.GetString(first);
        json.Should().Contain("http_download");
        json.Should().Contain("https://ex.com/vc_redist.x64.exe");
        json.Should().Contain("aaaabbbbccccdddd");
    }
}
