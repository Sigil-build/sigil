namespace SigilBuild.Signing.IntegrationTests;

using System;
using System.IO;

/// <summary>
/// Reports a genuine Skipped result when the Azure Trusted Signing live-tenant
/// preconditions are absent, instead of returning early and reporting as Passed
/// (register row R6). Covers both early <c>return</c> sites the original
/// <c>Signs_artifact_against_live_tenant</c> theory had:
/// <list type="bullet">
///   <item><description>the opt-in flag and Service-Principal/endpoint env vars
///   (shared by every format, formerly the outer <c>ShouldRun()</c> gate);</description></item>
///   <item><description>the per-format test artifact env var (formerly the inner
///   "no test artifact provided for this format" gate).</description></item>
/// </list>
/// A single <c>[Theory]</c> cannot report a different Skip per <c>[InlineData]</c>
/// row in xunit v2 — <c>Skip</c> is resolved once, at discovery, before any row's
/// data is bound — so the per-format artifact env var is passed to this attribute's
/// constructor and the theory is split into one <c>[Fact]</c> per format
/// (<see cref="AzureTrustedSigningTests"/>) instead. The assertions inside the
/// shared helper are unchanged; only the invocation shape changed, to make the
/// per-format skip real rather than a swallowed early return.
/// </summary>
internal sealed class AzureTrustedSigningFactAttribute : FactAttribute
{
    public AzureTrustedSigningFactAttribute(string artifactEnvVar)
    {
        if (Environment.GetEnvironmentVariable("SIGIL_AZURE_TS_INTEGRATION") != "1")
        {
            Skip = "Azure Trusted Signing integration test: SIGIL_AZURE_TS_INTEGRATION is not set to 1";
        }
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_TENANT_ID"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIGIL_AZURE_TS_ENDPOINT"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIGIL_AZURE_TS_ACCOUNT"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIGIL_AZURE_TS_PROFILE")))
        {
            Skip = "Azure Trusted Signing integration test: Service Principal / endpoint env vars "
                 + "(AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET, SIGIL_AZURE_TS_ENDPOINT, "
                 + "SIGIL_AZURE_TS_ACCOUNT, SIGIL_AZURE_TS_PROFILE) are not fully populated";
        }
        else
        {
            var artifactPath = Environment.GetEnvironmentVariable(artifactEnvVar);
            if (string.IsNullOrEmpty(artifactPath) || !File.Exists(artifactPath))
            {
                Skip = $"Azure Trusted Signing integration test: no test artifact provided via {artifactEnvVar}";
            }
        }
    }
}
