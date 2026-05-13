using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Signing.Azure;

// Token cache is **memory only** (per WBS 3.7 / sprint-07): no disk persistence,
// process-scoped. Persisted/keychain caches are explicitly out of MVP scope.
public class AzureCredentialProvider
{
    private static readonly string[] s_scopes = ["https://codesigning.azure.net/.default"];

    private readonly AzureTrustedSigningConfig _config;
    private readonly Func<TokenCredential> _credentialFactory;
    private AccessToken? _cachedToken;
    private readonly object _lock = new();

    public AzureCredentialProvider(AzureTrustedSigningConfig config)
        : this(config, credentialFactory: null) { }

    public AzureCredentialProvider(AzureTrustedSigningConfig config, Func<TokenCredential>? credentialFactory)
    {
        _config = config;
        _credentialFactory = credentialFactory ?? CreateCredential;
    }

    public TokenCredential CreateCredential()
    {
        var tenant = Environment.GetEnvironmentVariable(_config.TenantIdEnv) ?? "";
        var clientId = Environment.GetEnvironmentVariable(_config.ClientIdEnv) ?? "";
        var secret = Environment.GetEnvironmentVariable(_config.ClientSecretEnv) ?? "";
        return new ClientSecretCredential(tenant, clientId, secret);
    }

    public virtual async ValueTask<string> GetAccessTokenAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_cachedToken is { } t && t.ExpiresOn.AddMinutes(-5) > DateTimeOffset.UtcNow)
                return t.Token;
        }

        var cred = _credentialFactory();
        var ctx = new TokenRequestContext(s_scopes);
        var token = await cred.GetTokenAsync(ctx, ct);
        lock (_lock) _cachedToken = token;
        return token.Token;
    }
}
