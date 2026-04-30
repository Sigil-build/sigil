using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SigilBuild.Core.Configuration;

public static class SchemaValidator
{
    private const string DocsUrl = "https://docs.sigil.build/diagnostics/SIG0010";
    private const string SyntaxDocsUrl = "https://docs.sigil.build/diagnostics/SIG0001";

    private static JsonDocument? _cachedSchema;

    public static Task<IReadOnlyList<Diagnostic>> ValidateAsync(string yaml, string fileName)
    {
        _cachedSchema ??= JsonDocument.Parse(EmbeddedSchemas.LoadSigilSchemaJson());
        var diagnostics = new List<Diagnostic>();

        string json;
        try
        {
            json = YamlToJson(yaml);
        }
        catch (YamlException ex)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, DiagnosticCodes.YamlSyntaxError, ex.Message,
                new SourceLocation(fileName, (int)ex.Start.Line, (int)ex.Start.Column),
                SyntaxDocsUrl));
            return Task.FromResult<IReadOnlyList<Diagnostic>>(diagnostics);
        }

        using var doc = JsonDocument.Parse(json);
        Validate(doc.RootElement, _cachedSchema.RootElement, "", fileName, diagnostics);

        return Task.FromResult<IReadOnlyList<Diagnostic>>(diagnostics);
    }

    private static void Validate(JsonElement value, JsonElement schema, string path, string file, List<Diagnostic> diags)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("type", out var typeProp))
        {
            if (!MatchesType(value, typeProp.GetString()))
            {
                Report(diags, path, $"expected type '{typeProp.GetString()}' but got {value.ValueKind}", file);
                return;
            }
        }

        if (schema.TryGetProperty("const", out var constProp))
        {
            if (!JsonElementEquals(value, constProp))
                Report(diags, path, $"value must equal {constProp.GetRawText()}", file);
        }

        if (schema.TryGetProperty("enum", out var enumProp) && enumProp.ValueKind == JsonValueKind.Array)
        {
            var ok = enumProp.EnumerateArray().Any(e => JsonElementEquals(value, e));
            if (!ok)
            {
                var allowed = string.Join(", ", enumProp.EnumerateArray().Select(e => e.GetRawText()));
                Report(diags, path, $"value must be one of [{allowed}]", file);
            }
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object: ValidateObject(value, schema, path, file, diags); break;
            case JsonValueKind.Array: ValidateArray(value, schema, path, file, diags); break;
            case JsonValueKind.String: ValidateString(value, schema, path, file, diags); break;
            case JsonValueKind.Number: ValidateNumber(value, schema, path, file, diags); break;
        }

        if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (var sub in allOf.EnumerateArray()) Validate(value, sub, path, file, diags);
        }

        if (schema.TryGetProperty("if", out var ifSchema))
        {
            var condDiags = new List<Diagnostic>();
            Validate(value, ifSchema, path, file, condDiags);
            if (condDiags.Count == 0 && schema.TryGetProperty("then", out var thenSchema))
                Validate(value, thenSchema, path, file, diags);
            else if (condDiags.Count > 0 && schema.TryGetProperty("else", out var elseSchema))
                Validate(value, elseSchema, path, file, diags);
        }
    }

    private static void ValidateObject(JsonElement value, JsonElement schema, string path, string file, List<Diagnostic> diags)
    {
        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in req.EnumerateArray())
            {
                var n = name.GetString();
                if (n is null) continue;
                if (!value.TryGetProperty(n, out _))
                    Report(diags, Join(path, n), "required property missing", file);
            }
        }

        var props = schema.TryGetProperty("properties", out var pp) && pp.ValueKind == JsonValueKind.Object ? pp : default;
        var hasProps = props.ValueKind == JsonValueKind.Object;
        var additionalAllowed = !schema.TryGetProperty("additionalProperties", out var addProp)
            || addProp.ValueKind != JsonValueKind.False;

        foreach (var entry in value.EnumerateObject())
        {
            var childPath = Join(path, entry.Name);
            if (hasProps && props.TryGetProperty(entry.Name, out var childSchema))
            {
                Validate(entry.Value, childSchema, childPath, file, diags);
            }
            else if (!additionalAllowed)
            {
                Report(diags, childPath, "additional property not allowed", file);
            }
        }
    }

    private static void ValidateArray(JsonElement value, JsonElement schema, string path, string file, List<Diagnostic> diags)
    {
        var items = value.EnumerateArray().ToArray();
        if (schema.TryGetProperty("minItems", out var mi) && items.Length < mi.GetInt32())
            Report(diags, path, $"array must contain at least {mi.GetInt32()} item(s)", file);
        if (schema.TryGetProperty("maxItems", out var mx) && items.Length > mx.GetInt32())
            Report(diags, path, $"array must contain at most {mx.GetInt32()} item(s)", file);
        if (schema.TryGetProperty("uniqueItems", out var uq) && uq.ValueKind == JsonValueKind.True)
        {
            for (var i = 0; i < items.Length; i++)
                for (var j = i + 1; j < items.Length; j++)
                    if (JsonElementEquals(items[i], items[j]))
                    {
                        Report(diags, path, "array items must be unique", file);
                        i = items.Length; break;
                    }
        }
        if (schema.TryGetProperty("items", out var itemSchema))
        {
            for (var i = 0; i < items.Length; i++)
                Validate(items[i], itemSchema, $"{path}[{i}]", file, diags);
        }
    }

    private static void ValidateString(JsonElement value, JsonElement schema, string path, string file, List<Diagnostic> diags)
    {
        var s = value.GetString() ?? "";
        if (schema.TryGetProperty("minLength", out var ml) && s.Length < ml.GetInt32())
            Report(diags, path, $"string is shorter than minimum length {ml.GetInt32()}", file);
        if (schema.TryGetProperty("maxLength", out var mxl) && s.Length > mxl.GetInt32())
            Report(diags, path, $"string is longer than maximum length {mxl.GetInt32()}", file);
        if (schema.TryGetProperty("pattern", out var pat))
        {
            var rx = pat.GetString();
            if (rx is not null && !Regex.IsMatch(s, rx))
                Report(diags, path, $"string does not match pattern {rx}", file);
        }
        if (schema.TryGetProperty("format", out var fmt) && fmt.GetString() == "uri")
        {
            if (!System.Uri.TryCreate(s, System.UriKind.Absolute, out _))
                Report(diags, path, "string is not a valid URI", file);
        }
    }

    private static void ValidateNumber(JsonElement value, JsonElement schema, string path, string file, List<Diagnostic> diags)
    {
        if (!value.TryGetDouble(out var n)) return;
        if (schema.TryGetProperty("minimum", out var mn) && n < mn.GetDouble())
            Report(diags, path, $"value below minimum {mn.GetDouble()}", file);
        if (schema.TryGetProperty("maximum", out var mx) && n > mx.GetDouble())
            Report(diags, path, $"value above maximum {mx.GetDouble()}", file);
    }

    private static bool MatchesType(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        _ => true,
    };

    private static bool JsonElementEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetRawText() == b.GetRawText(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText(),
        };
    }

    private static void Report(List<Diagnostic> diags, string path, string message, string file)
    {
        diags.Add(new Diagnostic(
            DiagnosticSeverity.Error, DiagnosticCodes.SchemaViolation,
            string.IsNullOrEmpty(path) ? message : $"{path}: {message}",
            new SourceLocation(file, 0, 0),
            DocsUrl));
    }

    private static string Join(string parent, string child) =>
        string.IsNullOrEmpty(parent) ? child : $"{parent}.{child}";

    private static string YamlToJson(string yamlText)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yamlText));
        if (stream.Documents.Count == 0) return "null";

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
            WriteNode(writer, stream.Documents[0].RootNode);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter writer, YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                writer.WriteStartObject();
                foreach (var kv in map.Children)
                {
                    writer.WritePropertyName(((YamlScalarNode)kv.Key).Value ?? string.Empty);
                    WriteNode(writer, kv.Value);
                }
                writer.WriteEndObject();
                break;
            case YamlSequenceNode seq:
                writer.WriteStartArray();
                foreach (var child in seq.Children) WriteNode(writer, child);
                writer.WriteEndArray();
                break;
            case YamlScalarNode scalar:
                WriteScalar(writer, scalar);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null) { writer.WriteNullValue(); return; }

        if (scalar.Style is ScalarStyle.DoubleQuoted or ScalarStyle.SingleQuoted)
        {
            writer.WriteStringValue(value);
            return;
        }

        if (value.Length == 0 || value == "~" || value.Equals("null", System.StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteNullValue();
            return;
        }
        if (bool.TryParse(value, out var b)) { writer.WriteBooleanValue(b); return; }
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            writer.WriteNumberValue(i);
            return;
        }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            writer.WriteNumberValue(d);
            return;
        }
        writer.WriteStringValue(value);
    }
}
