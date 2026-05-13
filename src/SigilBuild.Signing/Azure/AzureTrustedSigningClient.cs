using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Signing.Azure;

public sealed record SubmitJobRequest(
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("digestAlgorithm")] string DigestAlgorithm);

public sealed record SubmitJobResponse(
    [property: JsonPropertyName("jobId")] string JobId);

public sealed record JobStatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("signature")] string? Signature);

[JsonSerializable(typeof(SubmitJobRequest))]
[JsonSerializable(typeof(SubmitJobResponse))]
[JsonSerializable(typeof(JobStatusResponse))]
internal sealed partial class AzureSigningJsonContext : JsonSerializerContext { }

public sealed class AzureTrustedSigningClient
{
    private readonly HttpClient _http;
    private readonly AzureTrustedSigningConfig _config;
    private readonly AzureCredentialProvider _credentials;

    public AzureTrustedSigningClient(HttpClient http, AzureTrustedSigningConfig config, AzureCredentialProvider credentials)
    {
        _http = http;
        _config = config;
        _credentials = credentials;
    }

    public async Task<string> SubmitAsync(byte[] artifactBytes, CancellationToken ct)
    {
        var digest = SHA256.HashData(artifactBytes);
        var body = new SubmitJobRequest(Convert.ToBase64String(digest), "SHA256");

        var token = await _credentials.GetAccessTokenAsync(ct);
        var url = $"{_config.Endpoint.TrimEnd('/')}/codesigningaccounts/{_config.AccountName}/certificateprofiles/{_config.CertificateProfile}/sign";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, AzureSigningJsonContext.Default.SubmitJobRequest),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(AzureSigningJsonContext.Default.SubmitJobResponse, ct)
            ?? throw new InvalidOperationException("empty submit response");
        return result.JobId;
    }

    public async Task<JobStatusResponse> GetStatusAsync(string jobId, CancellationToken ct)
    {
        var token = await _credentials.GetAccessTokenAsync(ct);
        var url = $"{_config.Endpoint.TrimEnd('/')}/jobs/{jobId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(AzureSigningJsonContext.Default.JobStatusResponse, ct)
            ?? throw new InvalidOperationException("empty status response");
    }
}
