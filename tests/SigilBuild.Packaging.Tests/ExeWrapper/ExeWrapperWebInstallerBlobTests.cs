using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// P12 / T12.5: the web-installer stub's synthesized blob
/// (<see cref="ExeWrapperPackager.BuildWebStubBlobBytes"/>) — construction only.
/// The live download-and-run of the stub is CI-VM-only (T12.6); these tests
/// assert the STUB's construction: exactly one <c>http_download</c> to the
/// package URL carrying the full package's sha256, followed by exactly one
/// <c>run_program</c> of the downloaded file, and that packing is deterministic
/// (no timestamp/GUID baked in).
/// </summary>
public class ExeWrapperWebInstallerBlobTests
{
    private const string PackageUrl = "https://cdn.example.com/acme/Acme-3.2.0-x64-Setup.exe";
    private const string PackageSha256 = "aaaabbbbccccddddaaaabbbbccccddddaaaabbbbccccddddaaaabbbbcccc0000";
    private const string FullPackageFileName = "Acme-3.2.0-x64-Setup.exe";

    private static SerializableWrapperBlob Deserialize(byte[] blob) =>
        JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(blob),
            WrapperBlobJsonContext.Default.SerializableWrapperBlob)!;

    private static SigilManifest Manifest() => new(
        "v1.0",
        new AppSection("com.acme.Studio", "Acme Studio", "3.2.0", "Acme, Inc.", null, null),
        new BuildSection("./out", null, null, true),
        null, null, null, null,
        Installer: null,
        Location: SourceLocation.Unknown);

    [Fact]
    public void Stub_blob_has_exactly_one_http_download_and_one_run_program()
    {
        var blob = Deserialize(ExeWrapperPackager.BuildWebStubBlobBytes(
            Manifest(), PackageUrl, PackageSha256, FullPackageFileName));

        blob.InstallSteps.Should().HaveCount(2, "the stub's only install action is download-then-run");

        var download = blob.InstallSteps.Should().ContainSingle(s => s.Type == "http_download").Subject;
        download.HttpUrl.Should().Be(PackageUrl);
        download.Sha256.Should().Be(PackageSha256, "the stub must verify against the ACTUAL full-package sha256");

        var run = blob.InstallSteps.Should().ContainSingle(s => s.Type == "run_program").Subject;
        run.Program.Should().Be(download.HttpDest, "the run step must target exactly what the download step just wrote");
    }

    [Fact]
    public void Download_dest_is_a_temp_dir_token_not_a_baked_in_path()
    {
        var blob = Deserialize(ExeWrapperPackager.BuildWebStubBlobBytes(
            Manifest(), PackageUrl, PackageSha256, FullPackageFileName));

        var download = blob.InstallSteps.Single(s => s.Type == "http_download");
        download.HttpDest.Should().StartWith("{temp_dir}/",
            "the destination must be resolvable at INSTALL time, not a pack-time temp path (which would break determinism)");
        download.HttpDest.Should().EndWith(FullPackageFileName);
    }

    [Fact]
    public void Download_url_is_https_only_and_run_program_waits_for_completion()
    {
        var blob = Deserialize(ExeWrapperPackager.BuildWebStubBlobBytes(
            Manifest(), PackageUrl, PackageSha256, FullPackageFileName));

        blob.InstallSteps.Single(s => s.Type == "http_download").HttpUrl.Should().StartWith("https://");
        blob.InstallSteps.Single(s => s.Type == "run_program").Wait.Should().BeTrue(
            "the stub must wait for the full install to finish before it exits");
    }

    [Fact]
    public void Stub_blob_carries_no_app_specific_parameters_or_options()
    {
        var blob = Deserialize(ExeWrapperPackager.BuildWebStubBlobBytes(
            Manifest(), PackageUrl, PackageSha256, FullPackageFileName));

        blob.Parameters.Should().BeEmpty();
        blob.PreInstall.Should().BeEmpty();
        blob.PostInstall.Should().BeEmpty();
    }

    /// <summary>
    /// CRITICAL (post-review fix): the stub's blob MUST be marked a delegating
    /// trampoline so <c>InstallSession</c> skips its OWN completion bookkeeping
    /// (ARP register / uninstall.exe copy / UninstallStateStore.Save) on
    /// success — the child Setup.exe it downloads and runs already does all of
    /// that correctly for the SAME AppId/scope. Without this flag the stub's
    /// own successful run would clobber the child's real uninstall state. The
    /// embedded-payload path (<see cref="ExeWrapperPackager.BuildBlobBytes"/>)
    /// must NOT set it — covered by <c>ExeWrapperHttpDownloadDeterminismTests</c>
    /// / <c>ExeWrapperOptionStepTests</c> style embedded blobs, which never
    /// touch this field and therefore default to false.
    /// </summary>
    [Fact]
    public void Stub_blob_is_marked_a_delegating_trampoline()
    {
        var blob = Deserialize(ExeWrapperPackager.BuildWebStubBlobBytes(
            Manifest(), PackageUrl, PackageSha256, FullPackageFileName));

        blob.IsDelegatingStub.Should().BeTrue(
            "InstallSession must skip its own ARP/uninstall-state bookkeeping when the stub's " +
            "run_program hand-off already delegated the real install to the downloaded child Setup.exe");
    }

    [Fact]
    public void Run_program_step_accepts_the_reboot_required_exit_code()
    {
        var blob = Deserialize(ExeWrapperPackager.BuildWebStubBlobBytes(
            Manifest(), PackageUrl, PackageSha256, FullPackageFileName));

        var run = blob.InstallSteps.Single(s => s.Type == "run_program");
        run.ExpectedExitCodes.Should().Contain(new[] { 0, 3010 },
            "a child Setup.exe returning 3010 (success, reboot required) must not spuriously fail the stub");
    }

    [Fact]
    public void BuildWebStubBlobBytes_is_byte_identical_across_two_builds_of_the_same_input()
    {
        var manifest = Manifest();

        var first = ExeWrapperPackager.BuildWebStubBlobBytes(manifest, PackageUrl, PackageSha256, FullPackageFileName);
        var second = ExeWrapperPackager.BuildWebStubBlobBytes(manifest, PackageUrl, PackageSha256, FullPackageFileName);

        first.Should().Equal(second,
            "two packs of the same manifest + package URL + sha256 must be byte-identical (no timestamp/GUID baked in)");
    }

    [Fact]
    public void Different_package_sha256_produces_a_different_blob()
    {
        var manifest = Manifest();

        var first = ExeWrapperPackager.BuildWebStubBlobBytes(manifest, PackageUrl, PackageSha256, FullPackageFileName);
        var second = ExeWrapperPackager.BuildWebStubBlobBytes(
            manifest, PackageUrl, "1111222233334444111122223333444411112222333344441111222233330000", FullPackageFileName);

        first.Should().NotEqual(second, "the embedded sha256 must reflect whatever the caller actually built");
    }
}
