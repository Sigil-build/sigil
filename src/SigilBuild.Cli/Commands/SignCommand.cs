using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Signing;
using SigilBuild.Signing.Audit;
using SigilBuild.Signing.Azure;
using SigilBuild.Signing.Local;

namespace SigilBuild.Cli.Commands;

public static class SignCommand
{
    public static Command Build()
    {
        var pathArg = new Argument<string>("path", () => "sigil.yaml", "Path to manifest");
        var artifactOpt = new Option<string>("--artifact", "Artifact file to sign") { IsRequired = true };

        var cmd = new Command("sign", "Sign an artifact per the manifest's sign provider");
        cmd.AddArgument(pathArg);
        cmd.AddOption(artifactOpt);
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var path = ctx.ParseResult.GetValueForArgument(pathArg);
            var artifact = ctx.ParseResult.GetValueForOption(artifactOpt)!;
            var ct = ctx.GetCancellationToken();

            var load = await ManifestLoader.LoadAsync(path, new ProcessEnvironmentReader());
            DiagnosticReporter.Write(Console.Error, load.Diagnostics, useColor: false);
            if (load.Manifest is null) { ctx.ExitCode = 1; return; }

            var sign = load.Manifest.Sign;
            if (sign is null || sign.Provider == SignProvider.None)
            {
                Console.Out.WriteLine("sign.provider = none; nothing to do");
                ctx.ExitCode = 0;
                return;
            }

            ISigningProvider provider = sign.Provider switch
            {
                SignProvider.Local => new LocalPfxSigner(sign.Local!),
                SignProvider.AzureTrustedSigning => new AzureTrustedSigner(
                    sign.AzureTrustedSigning!,
                    new HttpClient(),
                    new QuotaTracker(System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".sigil", "quota.json"))),
                _ => throw new InvalidOperationException($"unsupported provider {sign.Provider}"),
            };

            var result = await provider.SignAsync(new SignOptions(artifact, ProduceDetachedSignature: true), ct);
            DiagnosticReporter.Write(Console.Error, result.Diagnostics, useColor: false);

            var audit = AuditLog.Default();
            var hash = Convert.ToHexString(
                SHA256.HashData(await System.IO.File.ReadAllBytesAsync(artifact, ct)));
            await audit.AppendAsync(new AuditEntry(
                DateTimeOffset.UtcNow, provider.Name, artifact, hash, result.Thumbprint,
                result.Success ? "success" : "failure",
                result.Success ? null : "see diagnostics"), ct);

            ctx.ExitCode = result.Success ? 0 : 1;
            if (result.Success)
                Console.Out.WriteLine($"signed {artifact} via {provider.Name}");
        });

        return cmd;
    }
}
