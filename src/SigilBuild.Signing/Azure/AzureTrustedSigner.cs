using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Signing.Azure;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "Orchestrates a live Azure Trusted Signing tenant via REST + ClientSecretCredential; exercised only in the manual integration runbook with real secrets.")]
public sealed class AzureTrustedSigner : ISigningProvider
{
    private readonly AzureTrustedSigningConfig _config;
    private readonly HttpClient _http;
    private readonly QuotaTracker _quota;

    public AzureTrustedSigner(AzureTrustedSigningConfig config, HttpClient http, QuotaTracker quota)
    {
        _config = config; _http = http; _quota = quota;
    }

    public string Name => "azure-trusted-signing";

    public async Task<SignResult> SignAsync(SignOptions options, CancellationToken ct)
    {
        try
        {
            var creds = new AzureCredentialProvider(_config);
            var client = new AzureTrustedSigningClient(_http, _config, creds);
            var poller = new SigningJobPoller(client);

            var bytes = await File.ReadAllBytesAsync(options.ArtifactPath, ct);
            var jobId = await client.SubmitAsync(bytes, ct);
            var status = await poller.WaitAsync(jobId, ct);
            if (status.Status != "succeeded" || string.IsNullOrEmpty(status.Signature))
            {
                return new SignResult(false, null, null, null, new[]
                {
                    new Diagnostic(DiagnosticSeverity.Error, "SIG0300",
                        $"Azure signing job {jobId} reported {status.Status}",
                        SourceLocation.Unknown,
                        "https://docs.sigil.build/diagnostics/SIG0300"),
                });
            }

            // For MSIX, we need to embed the signature; signtool with /dlib pointing
            // at the Azure DLib is the supported path. For ZIP we write a detached .sig.
            var sigPath = options.ArtifactPath + ".sig";
            await File.WriteAllBytesAsync(sigPath, Convert.FromBase64String(status.Signature), ct);

            _quota.RecordSign(DateTimeOffset.UtcNow);
            return new SignResult(true, sigPath, null, null, Array.Empty<Diagnostic>());
        }
        catch (Exception ex)
        {
            return new SignResult(false, null, null, null, new[]
            {
                new Diagnostic(DiagnosticSeverity.Error, "SIG0301", ex.Message,
                    SourceLocation.Unknown,
                    "https://docs.sigil.build/diagnostics/SIG0301"),
            });
        }
    }
}
