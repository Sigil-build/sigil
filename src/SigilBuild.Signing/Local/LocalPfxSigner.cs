using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Signing.Local;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "Orchestrates SignToolRunner over signtool.exe + a real PFX; exercised only in the manual Windows integration runbook.")]
public sealed class LocalPfxSigner : ISigningProvider
{
    private readonly LocalSignConfig _config;
    private readonly Func<string?> _passwordResolver;

    public LocalPfxSigner(LocalSignConfig config, Func<string?>? passwordResolver = null)
    {
        _config = config;
        _passwordResolver = passwordResolver ??
            (() => string.IsNullOrEmpty(config.PasswordEnv) ? null : Environment.GetEnvironmentVariable(config.PasswordEnv));
    }

    public string Name => "local";

    public async Task<SignResult> SignAsync(SignOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SignResult(false, null, null, null, new[]
            {
                new Diagnostic(DiagnosticSeverity.Error, "SIG0200",
                    "local PFX signing requires Windows (uses signtool.exe)",
                    SourceLocation.Unknown,
                    "https://docs.sigil.build/diagnostics/SIG0200"),
            });
        }

        var pwd = _passwordResolver();
        using var cert = string.IsNullOrEmpty(pwd)
            ? X509CertificateLoader.LoadPkcs12FromFile(_config.Pfx, password: null)
            : X509CertificateLoader.LoadPkcs12FromFile(_config.Pfx, pwd, X509KeyStorageFlags.EphemeralKeySet);

        var validation = CertificateValidator.Validate(cert);
        if (!validation.IsValid)
        {
            return new SignResult(false, null, cert.Thumbprint, null, new[]
            {
                new Diagnostic(DiagnosticSeverity.Error, "SIG0210", validation.Reason,
                    SourceLocation.Unknown,
                    "https://docs.sigil.build/diagnostics/SIG0210"),
            });
        }

        var runner = SignToolRunner.FromSdk();
        var diagnostics = new List<Diagnostic>();

        foreach (var tsa in TimestampAuthority.Candidates(_config.TimestampUrl))
        {
            var run = await runner.SignWithPfxAsync(
                options.ArtifactPath, _config.Pfx, pwd, tsa, ct);
            if (run.ExitCode == 0)
                return new SignResult(true, options.ArtifactPath, cert.Thumbprint, tsa, Array.Empty<Diagnostic>());
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "SIG0220",
                $"signtool.exe (TSA={tsa}) exited {run.ExitCode}: {run.StdErr.Trim()}",
                SourceLocation.Unknown,
                "https://docs.sigil.build/diagnostics/SIG0220"));
        }

        return new SignResult(false, null, cert.Thumbprint, null, diagnostics);
    }
}
