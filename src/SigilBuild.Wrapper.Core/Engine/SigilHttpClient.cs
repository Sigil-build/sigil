namespace SigilBuild.Wrapper.Engine;

using System;
using System.Net.Http;

/// <summary>
/// The single, AOT-safe <see cref="HttpClient"/> shared by every install-time
/// HTTP consumer (P4): the <c>http_download</c> step and the wizard's
/// dynamic-options loader. One client honors the <b>system proxy</b> (the default
/// <see cref="SocketsHttpHandler"/> behavior) and pools connections; per-request
/// timeouts are applied by the caller via a <see cref="System.Threading.CancellationToken"/>,
/// so the client itself has no global timeout.
/// </summary>
/// <remarks>
/// <see cref="HttpClient"/> is fully Native-AOT compatible — no reflection, no
/// source generators. The <see cref="UseForTesting"/> seam lets integration tests
/// point the download step at a local HTTPS server with a self-signed certificate
/// without weakening the production client's certificate validation.
/// </remarks>
public static class SigilHttpClient
{
    private static readonly Lazy<HttpClient> _default = new(CreateDefault);
    private static HttpClient? _override;

    /// <summary>The shared client — the test override when set, else the default.</summary>
    public static HttpClient Shared => _override ?? _default.Value;

    private static HttpClient CreateDefault()
    {
        // SocketsHttpHandler defaults to UseProxy=true with the system proxy, which
        // is what an enterprise install behind a corporate proxy needs.
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            // No global timeout: each request carries its own linked CTS so a
            // per-step timeout_seconds can be honored and a retry can restart.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Test seam (internal): swap in <paramref name="client"/> as the shared client
    /// for the lifetime of the returned scope. Disposing the scope restores the
    /// default. Not for production use.
    /// </summary>
    internal static IDisposable UseForTesting(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _override = client;
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => _override = null;
    }
}
