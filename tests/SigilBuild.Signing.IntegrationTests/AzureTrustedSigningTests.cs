namespace SigilBuild.Signing.IntegrationTests;

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Signing;
using SigilBuild.Signing.Azure;

/// <summary>
/// Azure Trusted Signing integration tests (Sprint 7, WBS 3.10). Drives the
/// real <see cref="AzureTrustedSigner"/> against a live tenant for each of
/// the three MVP packaging formats (MSIX, ZIP, EXE-wrapper).
/// </summary>
/// <remarks>
/// <para>
/// Reports a genuine Skipped result (via <see cref="AzureTrustedSigningFactAttribute"/>,
/// register row R6) unless every gating condition is met:
/// </para>
/// <list type="bullet">
///   <item><description><c>SIGIL_AZURE_TS_INTEGRATION=1</c> in the environment — opt-in flag for the live-tenant suite.</description></item>
///   <item><description><c>AZURE_TENANT_ID</c>, <c>AZURE_CLIENT_ID</c>, <c>AZURE_CLIENT_SECRET</c> populated (Service Principal with the <c>Trusted Signing Certificate Profile Signer</c> role on the test tenant).</description></item>
///   <item><description><c>SIGIL_AZURE_TS_ENDPOINT</c>, <c>SIGIL_AZURE_TS_ACCOUNT</c>, <c>SIGIL_AZURE_TS_PROFILE</c> populated.</description></item>
///   <item><description>A test-artifact path for the format under test (<c>SIGIL_AZURE_TS_TEST_MSIX</c>, <c>SIGIL_AZURE_TS_TEST_ZIP</c>, <c>SIGIL_AZURE_TS_TEST_EXE</c>).</description></item>
/// </list>
/// <para>
/// Previously a single <c>[Theory]</c> with three <c>[InlineData]</c> rows. Converted
/// to three discrete <c>[Fact]</c>s (R6 fix round 1): xunit v2's <c>Skip</c> is
/// resolved once per attribute instance at discovery, before any row's data is bound,
/// so a <c>[Theory]</c> cannot report a different Skip reason per row — the missing
/// per-format artifact precondition could not become a real, per-row Skipped result
/// while it stayed a Theory. Splitting into one Fact per format (each carrying its own
/// artifact env var at compile time) lets <see cref="AzureTrustedSigningFactAttribute"/>
/// report the correct reason per format. The shared body and its assertions are
/// unchanged.
/// </para>
/// <para>
/// The test never persists secrets to disk and tears down the produced
/// <c>.sig</c> file in cleanup. CI scheduling lives in
/// <c>.github/workflows/signing-integration.yml</c> (post-MVP); the
/// dev runbook is in <c>tests/SigilBuild.Signing.IntegrationTests/README.md</c>.
/// </para>
/// </remarks>
public class AzureTrustedSigningTests
{
    [AzureTrustedSigningFact("SIGIL_AZURE_TS_TEST_MSIX")]
    public Task Signs_msix_artifact_against_live_tenant() =>
        SignArtifactAsync("msix", "SIGIL_AZURE_TS_TEST_MSIX");

    [AzureTrustedSigningFact("SIGIL_AZURE_TS_TEST_ZIP")]
    public Task Signs_zip_artifact_against_live_tenant() =>
        SignArtifactAsync("zip", "SIGIL_AZURE_TS_TEST_ZIP");

    [AzureTrustedSigningFact("SIGIL_AZURE_TS_TEST_EXE")]
    public Task Signs_exe_wrapper_artifact_against_live_tenant() =>
        SignArtifactAsync("exe-wrapper", "SIGIL_AZURE_TS_TEST_EXE");

    private static async Task SignArtifactAsync(string format, string artifactEnvVar)
    {
        var artifactPath = Environment.GetEnvironmentVariable(artifactEnvVar)!;

        var config = new AzureTrustedSigningConfig(
            Endpoint: Environment.GetEnvironmentVariable("SIGIL_AZURE_TS_ENDPOINT")!,
            AccountName: Environment.GetEnvironmentVariable("SIGIL_AZURE_TS_ACCOUNT")!,
            CertificateProfile: Environment.GetEnvironmentVariable("SIGIL_AZURE_TS_PROFILE")!,
            TenantIdEnv: "AZURE_TENANT_ID",
            ClientIdEnv: "AZURE_CLIENT_ID",
            ClientSecretEnv: "AZURE_CLIENT_SECRET");

        using var http = new HttpClient();
        var quotaPath = Path.Combine(Path.GetTempPath(), $"sigil-quota-{Guid.NewGuid():N}.json");
        var quota = new QuotaTracker(quotaPath);
        var signer = new AzureTrustedSigner(config, http, quota);

        // Copy the artifact to a temp path so the test doesn't mutate the source fixture.
        var testCopy = Path.Combine(Path.GetTempPath(), $"sigil-ts-{format}-{Guid.NewGuid():N}{Path.GetExtension(artifactPath)}");
        File.Copy(artifactPath, testCopy);
        try
        {
            var result = await signer.SignAsync(
                new SignOptions(testCopy, ProduceDetachedSignature: true),
                CancellationToken.None);

            result.Success.Should().BeTrue($"Azure Trusted Signing must produce a signature for the {format} artifact; diagnostics: {string.Join("; ", System.Linq.Enumerable.Select(result.Diagnostics, d => d.Code + ' ' + d.Message))}");
            result.SignaturePath.Should().NotBeNullOrEmpty();
            File.Exists(result.SignaturePath!).Should().BeTrue($"detached .sig file must exist at {result.SignaturePath}");
        }
        finally
        {
            try { File.Delete(testCopy); } catch { /* best-effort */ }
            try { if (File.Exists(testCopy + ".sig")) File.Delete(testCopy + ".sig"); } catch { /* best-effort */ }
            try { File.Delete(quotaPath); } catch { /* best-effort */ }
        }
    }
}
