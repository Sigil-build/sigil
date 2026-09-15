// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

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
                new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.LocalSigningRequiresWindows,
                    "local PFX signing requires Windows (uses signtool.exe)",
                    SourceLocation.Unknown,
                    DiagnosticCodes.DocsUrl(DiagnosticCodes.LocalSigningRequiresWindows)),
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
                new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.SigningCertificateInvalid, validation.Reason,
                    SourceLocation.Unknown,
                    DiagnosticCodes.DocsUrl(DiagnosticCodes.SigningCertificateInvalid)),
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
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.SigntoolFailed,
                $"signtool.exe (TSA={tsa}) exited {run.ExitCode}: {run.StdErr.Trim()}",
                SourceLocation.Unknown,
                DiagnosticCodes.DocsUrl(DiagnosticCodes.SigntoolFailed)));
        }

        return new SignResult(false, null, cert.Thumbprint, null, diagnostics);
    }
}
