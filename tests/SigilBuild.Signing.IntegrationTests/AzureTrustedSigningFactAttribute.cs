namespace SigilBuild.Signing.IntegrationTests;

using System;
using System.IO;

/// <summary>
/// Reports a genuine Skipped result when the Azure Trusted Signing live-tenant
/// preconditions are absent, instead of returning early and reporting as Passed. (R6)
/// Two gates apply: the opt-in flag plus Service-Principal/endpoint env vars (shared
/// by every format), and the per-format test artifact env var. xunit v2 resolves
/// <c>Skip</c> once per <c>[Theory]</c> at discovery, before any row's data is bound,
/// so the per-format env var is passed to this attribute's constructor and the
/// theory is split into one <c>[Fact]</c> per format (<see cref="AzureTrustedSigningTests"/>).
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
