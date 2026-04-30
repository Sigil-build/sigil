using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SigilBuild.Core.Configuration;

public static class ManifestParser
{
    public static ParseResult Parse(string yaml, string fileName)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0)
            {
                return new ParseResult(null, new[]
                {
                    new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.YamlSyntaxError,
                        "manifest is empty",
                        new SourceLocation(fileName, 1, 1),
                        "https://docs.sigil.build/diagnostics/SIG0001"),
                });
            }

            var root = stream.Documents[0].RootNode as YamlMappingNode
                ?? throw new YamlException("root must be a mapping");

            var manifest = MapManifest(root, fileName);
            return new ParseResult(manifest, Array.Empty<Diagnostic>());
        }
        catch (YamlException ex)
        {
            return new ParseResult(null, new[]
            {
                new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.YamlSyntaxError,
                    ex.Message,
                    new SourceLocation(fileName, (int)ex.Start.Line, (int)ex.Start.Column),
                    "https://docs.sigil.build/diagnostics/SIG0001"),
            });
        }
    }

    private static SigilManifest MapManifest(YamlMappingNode root, string file)
    {
        var loc = new SourceLocation(file, (int)root.Start.Line, (int)root.Start.Column);
        return new SigilManifest(
            Spec: GetScalar(root, "spec") ?? "",
            App: MapApp(GetMapping(root, "app", required: true)!),
            Build: MapBuild(GetMapping(root, "build", required: true)!),
            Package: MapPackage(GetMapping(root, "package")),
            Sign: MapSign(GetMapping(root, "sign")),
            Publish: MapPublish(GetMapping(root, "publish")),
            Updates: MapUpdates(GetMapping(root, "updates")),
            Installer: MapInstaller(GetMapping(root, "installer")),
            Location: loc);
    }

    private static AppSection MapApp(YamlMappingNode node) => new(
        Id: GetScalar(node, "id") ?? "",
        Name: GetScalar(node, "name") ?? "",
        Version: GetScalar(node, "version") ?? "",
        Publisher: GetScalar(node, "publisher") ?? "",
        Description: GetScalar(node, "description"),
        Homepage: GetScalar(node, "homepage"));

    private static BuildSection MapBuild(YamlMappingNode node) => new(
        Source: GetScalar(node, "source") ?? "",
        Include: GetSequence(node, "include"),
        Exclude: GetSequence(node, "exclude"),
        Deterministic: GetBool(node, "deterministic", defaultValue: true));

    private static PackageSection? MapPackage(YamlMappingNode? node)
    {
        if (node is null) return null;
        var formats = GetSequence(node, "formats")?.Select(ParseFormat).ToArray()
            ?? new[] { PackageFormat.Zip };
        var arches = GetSequence(node, "architectures")?.Select(ParseArch).ToArray()
            ?? new[] { TargetArchitecture.X64 };
        var msix = GetMapping(node, "msix");
        return new PackageSection(formats, arches, msix is null ? null : new MsixOptions(
            Publisher: GetScalar(msix, "publisher"),
            Logo: GetScalar(msix, "logo"),
            Capabilities: GetSequence(msix, "capabilities")));
    }

    private static SignSection? MapSign(YamlMappingNode? node)
    {
        if (node is null) return null;
        var provider = GetScalar(node, "provider") switch
        {
            "local" => SignProvider.Local,
            "azure-trusted-signing" => SignProvider.AzureTrustedSigning,
            _ => SignProvider.None,
        };
        var local = GetMapping(node, "local");
        var azure = GetMapping(node, "azureTrustedSigning");
        return new SignSection(
            provider,
            local is null ? null : new LocalSignConfig(
                Pfx: GetScalar(local, "pfx") ?? "",
                PasswordEnv: GetScalar(local, "passwordEnv"),
                TimestampUrl: GetScalar(local, "timestampUrl") ?? "http://timestamp.digicert.com"),
            azure is null ? null : new AzureTrustedSigningConfig(
                Endpoint: GetScalar(azure, "endpoint") ?? "",
                AccountName: GetScalar(azure, "accountName") ?? "",
                CertificateProfile: GetScalar(azure, "certificateProfile") ?? "",
                TenantIdEnv: GetScalar(azure, "tenantIdEnv") ?? "AZURE_TENANT_ID",
                ClientIdEnv: GetScalar(azure, "clientIdEnv") ?? "AZURE_CLIENT_ID",
                ClientSecretEnv: GetScalar(azure, "clientSecretEnv") ?? "AZURE_CLIENT_SECRET"));
    }

    private static PublishSection? MapPublish(YamlMappingNode? node)
    {
        if (node is null) return null;
        var gh = GetMapping(node, "github");
        return new PublishSection(gh is null ? null : new GitHubPublishConfig(
            Repo: GetScalar(gh, "repo") ?? "",
            TagPrefix: GetScalar(gh, "tagPrefix") ?? "v",
            Draft: GetBool(gh, "draft", defaultValue: false)));
    }

    private static UpdatesSection? MapUpdates(YamlMappingNode? node)
    {
        if (node is null) return null;
        return new UpdatesSection(
            Channel: GetScalar(node, "channel") ?? "stable",
            ManifestUrl: GetScalar(node, "manifestUrl"),
            DeltaTargets: GetInt(node, "deltaTargets", defaultValue: 3),
            SigningKey: GetScalar(node, "signingKey"));
    }

    private static InstallerSection? MapInstaller(YamlMappingNode? node)
    {
        if (node is null) return null;
        var brand = GetMapping(node, "brand");
        return new InstallerSection(brand is null ? null : new InstallerBrand(
            Logo: GetScalar(brand, "logo"),
            Hero: GetScalar(brand, "hero"),
            PrimaryColor: GetScalar(brand, "primaryColor"),
            AccentColor: GetScalar(brand, "accentColor")));
    }

    private static PackageFormat ParseFormat(string s) => s switch
    {
        "msix" => PackageFormat.Msix,
        "zip" => PackageFormat.Zip,
        _ => throw new YamlException($"unknown package format '{s}'"),
    };

    private static TargetArchitecture ParseArch(string s) => s switch
    {
        "x64" => TargetArchitecture.X64,
        "arm64" => TargetArchitecture.Arm64,
        _ => throw new YamlException($"unknown architecture '{s}'"),
    };

    private static YamlMappingNode? GetMapping(YamlMappingNode parent, string key, bool required = false)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlMappingNode m)
            return m;
        if (required) throw new YamlException($"required mapping '{key}' is missing");
        return null;
    }

    private static string? GetScalar(YamlMappingNode parent, string key)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode s)
            return s.Value;
        return null;
    }

    private static int GetInt(YamlMappingNode parent, string key, int defaultValue)
    {
        var s = GetScalar(parent, key);
        return s is not null && int.TryParse(s, out var n) ? n : defaultValue;
    }

    private static bool GetBool(YamlMappingNode parent, string key, bool defaultValue)
    {
        var s = GetScalar(parent, key);
        return s is not null && bool.TryParse(s, out var b) ? b : defaultValue;
    }

    private static string[]? GetSequence(YamlMappingNode parent, string key)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlSequenceNode seq)
            return seq.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? "").ToArray();
        return null;
    }
}
