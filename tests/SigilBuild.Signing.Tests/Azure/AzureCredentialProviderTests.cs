using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Signing.Azure;
using Xunit;

namespace SigilBuild.Signing.Tests.Azure;

public sealed class AzureCredentialProviderTests
{
    private sealed class CountingCredential : TokenCredential
    {
        public int Calls { get; private set; }
        private readonly TimeSpan _lifetime;

        public CountingCredential(TimeSpan lifetime) { _lifetime = lifetime; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return new AccessToken("fake-token-" + Calls, DateTimeOffset.UtcNow.Add(_lifetime));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    [Fact]
    public async Task GetAccessTokenAsync_SecondCallWithinCacheWindow_ReusesCachedToken()
    {
        // Arrange: token lifetime > 5 minutes, so the second call should hit the cache.
        var cred = new CountingCredential(TimeSpan.FromHours(1));
        var provider = new AzureCredentialProvider(
            new AzureTrustedSigningConfig("https://x", "a", "p", "T", "C", "S"),
            credentialFactory: () => cred);

        // Act
        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);

        // Assert: token returned identically, factory hit only once.
        first.Should().Be("fake-token-1");
        second.Should().Be("fake-token-1");
        cred.Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_TokenExpiringSoon_RefreshesToken()
    {
        // Arrange: token expires within the 5-minute skew window.
        // The provider should consider it expired and request a new one.
        var cred = new CountingCredential(TimeSpan.FromMinutes(2));
        var provider = new AzureCredentialProvider(
            new AzureTrustedSigningConfig("https://x", "a", "p", "T", "C", "S"),
            credentialFactory: () => cred);

        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);

        first.Should().Be("fake-token-1");
        second.Should().Be("fake-token-2");
        cred.Calls.Should().Be(2);
    }
}
