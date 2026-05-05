using System.IO;
using System.Reflection;

namespace SigilBuild.Core.Configuration;

internal static class EmbeddedSchemas
{
    private const string SigilSchemaResource = "SigilBuild.Core.Configuration.Embedded.sigil-schema.json";

    public static string LoadSigilSchemaJson()
    {
        var asm = typeof(EmbeddedSchemas).Assembly;
        using var stream = asm.GetManifestResourceStream(SigilSchemaResource)
            ?? throw new InvalidDataException($"embedded resource '{SigilSchemaResource}' missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
