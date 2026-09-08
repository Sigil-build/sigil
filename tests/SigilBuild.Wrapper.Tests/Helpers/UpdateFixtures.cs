namespace SigilBuild.Wrapper.Tests.Helpers;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Update;

/// <summary>
/// Shared doubles for driving <see cref="UpdateRunner"/> without a network or a child
/// process. Mirrors what <c>StagedExecutionTests</c> and <c>DownloadedBinaryTrustTests</c>
/// each carry inline; those keep their copies deliberately (they must compile on a parent
/// commit), so this serves only tests written against the current tree.
/// </summary>
internal static class UpdateFixtures
{
    public const string ManifestUrl = "https://updates.example.com/acme/stable.json";

    /// <summary>
    /// In-memory replacement for R13's replay high water mark. Every test that drives
    /// <see cref="UpdateRunner"/> must pass one of these: the production store reads and
    /// WRITES <c>%ProgramData%\Sigil\&lt;AppId&gt;\update-sequence.txt</c>, and CI runs
    /// elevated, so a test on the default store would be mutating the runner's real
    /// machine state rather than its own fixture.
    /// </summary>
    public sealed class InMemorySequenceStore : IUpdateSequenceStore
    {
        private readonly System.Collections.Generic.Dictionary<string, long> _seen = new(StringComparer.Ordinal);

        public InMemorySequenceStore(long? initial = null)
        {
            if (initial is { } value)
            {
                _seen[Key("com.acme.Studio", InstallScope.Machine)] = value;
            }
        }

        public System.Collections.Generic.IReadOnlyDictionary<string, long> Recorded => _seen;

        private static string Key(string appId, InstallScope scope) => $"{appId}|{scope}";

        public long? Read(string appId, InstallScope scope) =>
            _seen.TryGetValue(Key(appId, scope), out var value) ? value : null;

        public void Record(string appId, InstallScope scope, long sequence, Action<string, bool>? report)
        {
            var key = Key(appId, scope);
            if (!_seen.TryGetValue(key, out var existing) || sequence > existing)
            {
                _seen[key] = sequence;
            }
        }
    }

    public static UpdateRequest Request(string signingKey, string tempDirectory) =>
        new(ManifestUrl: ManifestUrl, SigningKey: signingKey, Channel: "stable",
            Scope: InstallScope.Machine, AppId: "com.acme.Studio", TempDirectory: tempDirectory);

    public static UpgradeState Installed(string version) =>
        new(Found: true, InstalledVersion: version,
            PriorInstallDir: @"C:\Acme", PriorUninstallExe: @"C:\Acme\uninstall.exe",
            FoundScope: InstallScope.Machine);

    public static (byte[] Manifest, byte[] Signature, string PublicKeyBase64) SignedManifest(
        string version, string sha256)
    {
        // R13: freshness fields are required. "Minted now, valid for a week, sequence 1"
        // keeps every pre-R13 fixture behaving exactly as it did.
        var issued = DateTimeOffset.UtcNow;
        var json =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            $"  \"version\": \"{version}\",\n" +
            $"  \"packageUrl\": \"https://updates.example.com/acme/{version}/Setup.exe\",\n" +
            $"  \"issuedAt\": \"{issued:O}\",\n" +
            $"  \"expiresAt\": \"{issued.AddDays(7):O}\",\n" +
            "  \"sequence\": 1,\n" +
            $"  \"sha256\": \"{sha256}\"\n" +
            "}";
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var signature = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        return (bytes, Encoding.UTF8.GetBytes(Convert.ToBase64String(signature)), spki);
    }

    public static IUpdateResourceFetcher Fetcher(byte[] manifest, byte[] signature) =>
        new MappedFetcher(url => url.EndsWith(".sig", StringComparison.Ordinal)
            ? UpdateResourceResult.Ok(signature)
            : UpdateResourceResult.Ok(manifest));

    private sealed class MappedFetcher : IUpdateResourceFetcher
    {
        private readonly Func<string, UpdateResourceResult> _map;

        public MappedFetcher(Func<string, UpdateResourceResult> map) => _map = map;

        public Task<UpdateResourceResult> FetchAsync(string url, CancellationToken ct)
            => Task.FromResult(_map(url));
    }

    /// <summary>
    /// Puts real bytes on disk at the destination and reports success, so the runner faces
    /// the same file a real download would leave.
    /// </summary>
    public sealed class WritingDownloader : IUpdatePackageDownloader
    {
        private readonly byte[] _bytes;

        public WritingDownloader(byte[] bytes) => _bytes = bytes;

        public Task<UpdatePackageDownloadResult> DownloadAsync(
            string url, string destination, string sha256, CancellationToken ct)
        {
            File.WriteAllBytes(destination, _bytes);
            return Task.FromResult(UpdatePackageDownloadResult.Ok());
        }
    }
}
