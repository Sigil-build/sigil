using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Polly;
using SigilBuild.Core.Manifest;
using SigilBuild.Signing.Azure;
using Xunit;

namespace SigilBuild.Signing.Tests.Azure;

public sealed class AzureTrustedSigningClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Responder(request));
    }

    private sealed class FakeCreds : AzureCredentialProvider
    {
        public FakeCreds() : base(new AzureTrustedSigningConfig("https://x", "a", "p", "T", "C", "S")) { }
        public override ValueTask<string> GetAccessTokenAsync(CancellationToken ct) => new("fake-token");
    }

    [Fact]
    public async Task SubmitAsync_PostsDigestAndReturnsJobId()
    {
        var handler = new FakeHandler
        {
            Responder = req =>
            {
                req.Method.Should().Be(HttpMethod.Post);
                req.Headers.Authorization!.Scheme.Should().Be("Bearer");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { jobId = "abc-123" }),
                };
            },
        };
        var client = new AzureTrustedSigningClient(
            new HttpClient(handler),
            new AzureTrustedSigningConfig("https://eus.codesigning.azure.net", "acct", "prof", "T", "C", "S"),
            new FakeCreds());

        var id = await client.SubmitAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);

        id.Should().Be("abc-123");
    }

    [Fact]
    public async Task SubmitAsync_RetriesOn503_SucceedsOnThirdAttempt()
    {
        var attempts = 0;
        var handler = new FakeHandler
        {
            Responder = req =>
            {
                attempts++;
                if (attempts < 3)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { jobId = "retry-ok" }),
                };
            },
        };
        var retryHandler = new Microsoft.Extensions.Http.PolicyHttpMessageHandler(
            Polly.Extensions.Http.HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(5, attempt => TimeSpan.FromMilliseconds(10 * attempt)))
        { InnerHandler = handler };

        var client = new AzureTrustedSigningClient(
            new HttpClient(retryHandler),
            new AzureTrustedSigningConfig("https://eus.codesigning.azure.net", "acct", "prof", "T", "C", "S"),
            new FakeCreds());

        var id = await client.SubmitAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);

        id.Should().Be("retry-ok");
        attempts.Should().Be(3);
    }
}
