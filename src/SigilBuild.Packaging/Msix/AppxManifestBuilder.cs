using System;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Linq;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Packaging.Msix;

public static class AppxManifestBuilder
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    public static string Build(SigilManifest manifest, TargetArchitecture arch)
    {
        var msix = manifest.Package?.Msix ?? new MsixOptions(null, null, null);
        var quad = ToQuadVersion(manifest.App.Version);
        var publisher = msix.Publisher ?? $"CN={manifest.App.Publisher}";
        var archAttr = arch == TargetArchitecture.Arm64 ? "arm64" : "x64";

        var doc = new XDocument(
            new XElement(Ns + "Package",
                new XAttribute(XNamespace.Xmlns + "uap", Uap.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "rescap", Rescap.NamespaceName),
                new XElement(Ns + "Identity",
                    new XAttribute("Name", manifest.App.Id),
                    new XAttribute("Publisher", publisher),
                    new XAttribute("Version", quad),
                    new XAttribute("ProcessorArchitecture", archAttr)),
                new XElement(Ns + "Properties",
                    new XElement(Ns + "DisplayName", manifest.App.Name),
                    new XElement(Ns + "PublisherDisplayName", manifest.App.Publisher),
                    new XElement(Ns + "Logo", "Assets\\StoreLogo.png")),
                new XElement(Ns + "Dependencies",
                    new XElement(Ns + "TargetDeviceFamily",
                        new XAttribute("Name", "Windows.Desktop"),
                        new XAttribute("MinVersion", "10.0.17763.0"),
                        new XAttribute("MaxVersionTested", "10.0.22621.0"))),
                new XElement(Ns + "Resources",
                    new XElement(Ns + "Resource", new XAttribute("Language", "en-us"))),
                new XElement(Ns + "Applications",
                    new XElement(Ns + "Application",
                        new XAttribute("Id", "App"),
                        new XAttribute("Executable", $"{manifest.App.Name}.exe"),
                        new XAttribute("EntryPoint", "Windows.FullTrustApplication"),
                        new XElement(Uap + "VisualElements",
                            new XAttribute("DisplayName", manifest.App.Name),
                            new XAttribute("Description", manifest.App.Description ?? manifest.App.Name),
                            new XAttribute("BackgroundColor", "transparent"),
                            new XAttribute("Square150x150Logo", "Assets\\Square150x150Logo.png"),
                            new XAttribute("Square44x44Logo", "Assets\\Square44x44Logo.png")))),
                BuildCapabilities(msix.Capabilities ?? Array.Empty<string>())));

        // OmitXmlDeclaration=true: MakeAppx.exe rejects manifests that carry a BOM or
        // an XML declaration whose encoding attribute conflicts with the file's actual encoding.
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false) };
        var sb = new System.Text.StringBuilder();
        using (var w = XmlWriter.Create(sb, settings))
        {
            doc.Save(w);
            w.Flush();
        }
        return sb.ToString();
    }

    private static XElement BuildCapabilities(IReadOnlyList<string> caps)
    {
        var element = new XElement(Ns + "Capabilities");
        foreach (var c in caps)
            element.Add(new XElement(Ns + "Capability", new XAttribute("Name", c)));
        // runFullTrust is required for Windows.FullTrustApplication entry point.
        element.Add(new XElement(Rescap + "Capability", new XAttribute("Name", "runFullTrust")));
        return element;
    }

    private static string ToQuadVersion(string semver)
    {
        // Strip pre-release / build metadata, then pad to 4 parts.
        var dash = semver.IndexOf('-');
        var plus = semver.IndexOf('+');
        var cut = (dash, plus) switch
        {
            (>= 0, >= 0) => Math.Min(dash, plus),
            (>= 0, _) => dash,
            (_, >= 0) => plus,
            _ => semver.Length,
        };
        var core = semver[..cut];
        var parts = core.Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0.0",
            2 => $"{parts[0]}.{parts[1]}.0.0",
            3 => $"{parts[0]}.{parts[1]}.{parts[2]}.0",
            _ => string.Join('.', parts[..4]),
        };
    }
}
