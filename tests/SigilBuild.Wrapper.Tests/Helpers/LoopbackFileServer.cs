namespace SigilBuild.Wrapper.Tests.Helpers;

using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// Minimal HTTPS/1.1 origin over <see cref="SslStream"/> serving one fixed body, plus the
/// client-side trust scope that accepts its ephemeral certificate.
/// </summary>
/// <remarks>
/// The older lane tests each carry their own copy of this on purpose — so a whole test
/// file can be dropped onto a parent commit unchanged to watch it fail. This shared one
/// exists for tests that pin an invariant of the CURRENT tree and have no parent-commit
/// story to preserve; duplicating it a fourth time would buy nothing.
/// <para>
/// The certificate is ephemeral and in-memory. It is handed to <see cref="SslStream"/> and
/// disposed; it is never passed to <c>X509Store</c>, so no test using this mutates the
/// host's trust configuration.
/// </para>
/// </remarks>
internal sealed class LoopbackFileServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _cert;
    private readonly byte[] _body;

    public LoopbackFileServer(byte[] body)
    {
        _body = body;
        _cert = CreateSelfSignedCert();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public int Port { get; }

    public string Url(string path) => $"https://127.0.0.1:{Port}{path}";

    /// <summary>
    /// Point <see cref="SigilHttpClient"/> at a client that trusts THIS server's
    /// certificate by thumbprint, for the lifetime of the returned scope. The production
    /// client's certificate validation is untouched.
    /// </summary>
    public IDisposable Trust()
    {
        var thumbprint = _cert.Thumbprint;
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is X509Certificate2 c && c.Thumbprint == thumbprint,
            },
        };
        return SigilHttpClient.UseForTesting(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { return; } // listener stopped
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
#pragma warning disable CA1031 // test origin: swallow all per-connection errors
        try
        {
            using (client)
            await using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
            {
                await ssl.AuthenticateAsServerAsync(_cert, clientCertificateRequired: false,
                    checkCertificateRevocation: false).ConfigureAwait(false);

                _ = await ReadLineAsync(ssl).ConfigureAwait(false);
                while (!string.IsNullOrEmpty(await ReadLineAsync(ssl).ConfigureAwait(false))) { }

                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {_body.Length}\r\nConnection: close\r\n\r\n");
                await ssl.WriteAsync(header).ConfigureAwait(false);
                await ssl.WriteAsync(_body).ConfigureAwait(false);
                await ssl.FlushAsync().ConfigureAwait(false);
            }
        }
        catch { /* client gone / handshake aborted — ignore */ }
#pragma warning restore CA1031
    }

    private static async Task<string> ReadLineAsync(SslStream ssl)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await ssl.ReadAsync(one.AsMemory(0, 1)).ConfigureAwait(false);
            if (n == 0) break;
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') sb.Append((char)one[0]);
        }
        return sb.ToString();
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var ephemeral = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pfx = ephemeral.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        _listener.Stop();
        _cert.Dispose();
    }
}
