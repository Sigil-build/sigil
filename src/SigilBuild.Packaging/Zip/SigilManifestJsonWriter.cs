using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.Common;

namespace SigilBuild.Packaging.Zip;

public static class SigilManifestJsonWriter
{
    public static byte[] Build(SigilManifest manifest, IReadOnlyList<WalkedFile> files)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteString("spec", manifest.Spec);
        writer.WriteStartObject("app");
        writer.WriteString("id", manifest.App.Id);
        writer.WriteString("name", manifest.App.Name);
        writer.WriteString("version", manifest.App.Version);
        writer.WriteString("publisher", manifest.App.Publisher);
        writer.WriteEndObject();
        writer.WriteStartArray("files");
        foreach (var f in files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", f.RelativePath);
            writer.WriteNumber("size", f.Length);
            writer.WriteString("sha256", ManifestHasher.Sha256(f.AbsolutePath));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }
}
