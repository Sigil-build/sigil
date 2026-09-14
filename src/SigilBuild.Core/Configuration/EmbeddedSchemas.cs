// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

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
