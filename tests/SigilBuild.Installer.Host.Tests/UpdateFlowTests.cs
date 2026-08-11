using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Update;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// VM-level tests for the T12.4 headed <c>/Update</c> flow: <see cref="UpdateViewModel"/>
/// wired to a REAL <see cref="UpdateRunner"/> (the same production decision logic the
/// headless path drives) behind fake fetch/download/launch seams — mirroring how
/// <c>UpdateRunnerTests</c> exercises the runner directly, but proving the ViewModel's
/// state machine reacts correctly: checking → up-to-date (done), and
/// checking → downloading → launching the child (done), plus that a HEADED run
/// threads <c>SilentChild:false</c> through to the launched child's args — the
/// headless path's <c>SilentChild:true</c> is covered in <c>UpdateRunnerTests</c>.
/// </summary>
public sealed class UpdateFlowTests
{
    private const string ManifestUrl = "https://updates.example.com/acme/stable.json";

    /// <summary>
    /// The bytes <see cref="FakeDownloader"/> puts on disk, and their real digest. The
    /// double must leave a file the runner can re-open and re-verify (register row
    /// R12): the runner holds the staged package open across the child launch, so a
    /// downloader that reports success without ever writing anything is no longer a
    /// faithful stand-in for a real one.
    /// </summary>
    private static readonly byte[] PackageBytes = Encoding.UTF8.GetBytes("the-downloaded-setup-payload");

    private static readonly string PackageSha256 =
        Convert.ToHexString(SHA256.HashData(PackageBytes)).ToLowerInvariant();

    private sealed class FakeFetcher : IUpdateResourceFetcher
    {
        private readonly Func<string, UpdateResourceResult> _map;
        public FakeFetcher(Func<string, UpdateResourceResult> map) => _map = map;

        public Task<UpdateResourceResult> FetchAsync(string url, CancellationToken ct)
            => Task.FromResult(_map(url));
    }

    private sealed class FakeDownloader : IUpdatePackageDownloader
    {
        public bool Called { get; private set; }

        public Task<UpdatePackageDownloadResult> DownloadAsync(string url, string destination, string sha256, CancellationToken ct)
        {
            Called = true;
            // A successful download leaves the verified bytes on disk — the runner
            // re-opens and re-hashes them before it launches anything.
            System.IO.File.WriteAllBytes(destination, PackageBytes);
            return Task.FromResult(UpdatePackageDownloadResult.Ok());
        }
    }

    private sealed class FakeLauncher : IChildInstallerLauncher
    {
        private readonly int _exitCode;
        public bool Called { get; private set; }
        public IReadOnlyList<string>? Args { get; private set; }

        public FakeLauncher(int exitCode) => _exitCode = exitCode;

        public Task<int> RunAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct)
        {
            Called = true;
            Args = args;
            return Task.FromResult(_exitCode);
        }
    }

    private static (byte[] Manifest, byte[] Signature, string PublicKeyBase64) SignedManifest(string version)
    {
        var json =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            $"  \"version\": \"{version}\",\n" +
            $"  \"packageUrl\": \"https://updates.example.com/acme/{version}/Setup.exe\",\n" +
            $"  \"sha256\": \"{PackageSha256}\"\n" +
            "}";
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var sig = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        return (bytes, Encoding.UTF8.GetBytes(Convert.ToBase64String(sig)), spki);
    }

    private static FakeFetcher Fetcher(byte[] manifest, byte[] signature) =>
        new(url => url.EndsWith(".sig", StringComparison.Ordinal)
            ? UpdateResourceResult.Ok(signature)
            : UpdateResourceResult.Ok(manifest));

    private static UpgradeState Installed(string version) =>
        new(Found: true, InstalledVersion: version,
            PriorInstallDir: @"C:\Acme", PriorUninstallExe: @"C:\Acme\uninstall.exe",
            FoundScope: InstallScope.Machine);

    /// <summary>
    /// Wires the VM's runner delegate to a REAL <see cref="UpdateRunner"/> over fake
    /// seams, with <c>SilentChild:false</c> — exactly what
    /// <c>InstallSession.RunUpdateInteractiveAsync</c> (the headed entry point)
    /// passes, per its own remarks. The headless entry point's <c>SilentChild:true</c>
    /// is covered directly in <c>UpdateRunnerTests</c>.
    /// </summary>
    private static void ConfigureHeadedRunner(
        UpdateViewModel vm,
        IUpdateResourceFetcher fetcher,
        IUpdatePackageDownloader downloader,
        IChildInstallerLauncher launcher,
        UpgradeState installed,
        string? signingKey)
    {
        vm.ConfigureRunner((report, ct) =>
        {
            var runner = new UpdateRunner(fetcher, downloader, launcher, () => installed, report);
            var request = new UpdateRequest(
                ManifestUrl: ManifestUrl,
                SigningKey: signingKey,
                Channel: "stable",
                Scope: InstallScope.Machine,
                AppId: "com.acme.Studio",
                TempDirectory: System.IO.Path.GetTempPath(),
                SilentChild: false);
            return runner.RunAsync(request, ct);
        });
    }

    [Fact]
    public void Fresh_view_model_starts_on_the_checking_screen()
    {
        var vm = new UpdateViewModel(new BrandTokens { AppName = "Acme Studio" });

        vm.CurrentStep.Should().Be(UpdateStep.Checking);
        vm.IsChecking.Should().BeTrue();
        vm.IsProgress.Should().BeTrue();
        vm.UpdateTask.Should().BeNull("Start() has not been called yet");
    }

    [Fact]
    public async Task Up_to_date_run_lands_on_the_up_to_date_screen_with_exit_0()
    {
        var (manifest, sig, key) = SignedManifest("1.0.0");
        var downloader = new FakeDownloader();
        var launcher = new FakeLauncher(0);

        var vm = new UpdateViewModel(new BrandTokens { AppName = "Acme Studio" });
        // Installed version == channel version → Same → up to date, no download.
        ConfigureHeadedRunner(vm, Fetcher(manifest, sig), downloader, launcher, Installed("1.0.0"), key);

        vm.Start();
        await vm.UpdateTask!;

        vm.OutcomeExitCode.Should().Be(0);
        vm.CurrentStep.Should().Be(UpdateStep.UpToDate);
        vm.IsUpToDate.Should().BeTrue();
        vm.IsFinished.Should().BeTrue();
        vm.UpToDateMessage.Should().Contain("Acme Studio");
        downloader.Called.Should().BeFalse();
        launcher.Called.Should().BeFalse();
    }

    [Fact]
    public async Task Newer_version_downloads_and_launches_the_child_headed_without_silent()
    {
        var (manifest, sig, key) = SignedManifest("2.0.0");
        var downloader = new FakeDownloader();
        var launcher = new FakeLauncher(0);

        var vm = new UpdateViewModel(new BrandTokens { AppName = "Acme Studio" });
        ConfigureHeadedRunner(vm, Fetcher(manifest, sig), downloader, launcher, Installed("1.0.0"), key);

        vm.Start();
        await vm.UpdateTask!;

        vm.OutcomeExitCode.Should().Be(0);
        vm.CurrentStep.Should().Be(UpdateStep.Done, "a newer version was found, downloaded, and the launched child succeeded");
        vm.IsDone.Should().BeTrue();
        vm.IsFinished.Should().BeTrue();
        vm.DoneMessage.Should().Contain("Acme Studio");
        downloader.Called.Should().BeTrue();
        launcher.Called.Should().BeTrue();

        // T12.4: the HEADED path must launch the child WITHOUT /silent — only the
        // scope flag — so the user sees the new version's own install wizard.
        launcher.Args.Should().Equal("/allusers");
        launcher.Args.Should().NotContain("/silent");

        // The progress log (populated asynchronously via Progress<T>, like
        // UninstallViewModel's — poll instead of asserting immediately) shows the
        // run actually visited the downloading and launching-child stages.
        await WaitUntilAsync(() => vm.LogLines.Count >= 3);
        var messages = vm.LogLines.Select(l => l.Text).ToArray();
        messages.Should().Contain(m => m.Contains("newer version available"));
        messages.Should().Contain(m => m.Contains("running the downloaded setup"));
    }

    [Fact]
    public async Task Failed_check_lands_on_the_failed_screen()
    {
        var fetcher = new FakeFetcher(_ => UpdateResourceResult.Failed("connection refused"));
        var downloader = new FakeDownloader();
        var launcher = new FakeLauncher(0);

        var vm = new UpdateViewModel(new BrandTokens { AppName = "Acme Studio" });
        ConfigureHeadedRunner(vm, fetcher, downloader, launcher, Installed("1.0.0"), "any-key");

        vm.Start();
        await vm.UpdateTask!;

        vm.CurrentStep.Should().Be(UpdateStep.Failed);
        vm.IsFailed.Should().BeTrue();
        vm.IsFinished.Should().BeTrue();
        vm.ErrorMessage.Should().Contain("could not check for updates");
        downloader.Called.Should().BeFalse();
    }

    [Fact]
    public void Start_is_a_no_op_when_the_runner_is_not_configured()
    {
        var vm = new UpdateViewModel(new BrandTokens());

        vm.Start();

        vm.UpdateTask.Should().NotBeNull("Start() always creates the task, even when RunAsync no-ops immediately");
        vm.CurrentStep.Should().Be(UpdateStep.Checking, "with no runner wired the state machine never advances");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
