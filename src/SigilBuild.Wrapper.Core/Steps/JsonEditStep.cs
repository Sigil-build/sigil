namespace SigilBuild.Wrapper.Steps;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// <c>json_edit</c> step (P8, gap G9): set the value at an RFC 6901 JSON pointer in
/// a JSON file, creating intermediate objects/arrays as needed. Uses the
/// System.Text.Json <see cref="JsonNode"/> DOM (no reflection / no source-gen), so
/// it is AOT-safe. Journaled for byte-exact rollback. Output is re-serialized
/// pretty-printed — the original formatting is not preserved (documented).
/// </summary>
internal sealed class JsonEditStep : IStep
{
    private readonly InstallStep.JsonEdit _spec;

    public JsonEditStep(InstallStep.JsonEdit spec) => _spec = spec;

    public Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        var pointer = ctx.Resolve(_spec.JsonPointer);
        var value = ctx.Resolve(_spec.Value);

        var result = ConfigFileEditor.Edit(
            ctx, journal, _spec.Path, _spec.CreateIfMissing,
            current => JsonEditor.Set(current, pointer, value, _spec.ValueType),
            "json_edit", _spec.AllowOutsideInstallDir);

        return Task.FromResult(result);
    }
}

internal static class JsonEditor
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string Set(
        string? content, string pointer, string value,
        JsonValueType valueType = JsonValueType.Text)
    {
        JsonNode root = string.IsNullOrWhiteSpace(content)
            ? new JsonObject()
            : JsonNode.Parse(content!) ?? throw new InvalidOperationException("json_edit: file is not valid JSON");

        var tokens = ParsePointer(pointer);
        if (tokens.Count == 0)
        {
            throw new InvalidOperationException("json_edit: pointer must reference a member (whole-document replacement is unsupported)");
        }

        var node = root;
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            node = Descend(node, tokens[i], tokens[i + 1]);
        }
        SetFinal(node, tokens[^1], ToJsonNode(value, valueType));

        return root.ToJsonString(WriteOptions);
    }

    // RFC 6901: leading '/', tokens separated by '/', "~1" → "/", "~0" → "~".
    private static List<string> ParsePointer(string pointer)
    {
        if (pointer.Length == 0)
        {
            return new List<string>();
        }
        if (pointer[0] != '/')
        {
            throw new InvalidOperationException($"json_edit: pointer must start with '/' (got '{pointer}')");
        }
        var raw = pointer.Substring(1).Split('/');
        var result = new List<string>(raw.Length);
        foreach (var r in raw)
        {
            result.Add(r.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }
        return result;
    }

    private static JsonNode Descend(JsonNode node, string token, string nextToken)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj[token] is { } existing)
                {
                    return existing;
                }
                JsonNode created = IsIndex(nextToken) ? new JsonArray() : new JsonObject();
                obj[token] = created;
                return created;

            case JsonArray arr:
                var idx = ParseIndex(arr, token);
                while (arr.Count <= idx)
                {
                    arr.Add(null);
                }
                if (arr[idx] is { } child)
                {
                    return child;
                }
                JsonNode createdInArr = IsIndex(nextToken) ? new JsonArray() : new JsonObject();
                arr[idx] = createdInArr;
                return createdInArr;

            default:
                throw new InvalidOperationException($"json_edit: cannot descend into a scalar at token '{token}'");
        }
    }

    private static void SetFinal(JsonNode node, string token, JsonNode? value)
    {
        switch (node)
        {
            case JsonObject obj:
                obj[token] = value;
                return;
            case JsonArray arr:
                if (token == "-")
                {
                    arr.Add(value);
                    return;
                }
                var idx = ParseIndex(arr, token);
                while (arr.Count <= idx)
                {
                    arr.Add(null);
                }
                arr[idx] = value;
                return;
            default:
                throw new InvalidOperationException("json_edit: cannot set a member on a scalar");
        }
    }

    private static int ParseIndex(JsonArray arr, string token)
    {
        if (token == "-")
        {
            return arr.Count;
        }
        if (!int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var idx))
        {
            throw new InvalidOperationException($"json_edit: array index expected, got '{token}'");
        }
        return idx;
    }

    private static bool IsIndex(string token) =>
        token == "-" ||
        int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Turn the resolved <c>value</c> into the node to write, as declared by
    /// <c>value_type</c> (register row R35).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What changed and why.</b> This used to be unconditional literal inference:
    /// try <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>,
    /// keep whatever came back, fall back to a string only when the parse failed. That
    /// is defensible for a literal the publisher typed into the manifest and wrong for
    /// everything else. The same field also carries values resolved from a wizard field,
    /// a <c>registry_read</c> var or a <c>/P&lt;name&gt;=</c> argument, and those decide
    /// the SHAPE of the node written into the application's own configuration: supply
    /// <c>{"admin":true}</c> where the author wrote and reviewed a string, and the
    /// application reads an object. Encoding was never the flaw — the output is always
    /// well-formed JSON — the flaw is that the value's supplier picks the type.
    /// </para>
    /// <para>
    /// <b>String is the default</b>, so the inference is now opt-in. A manifest that
    /// genuinely means a number or a boolean says <c>value_type: json</c>, and gets a
    /// hard failure if the value does not parse — with the intent declared, a
    /// non-parsing value is a manifest error rather than a silent downgrade to a
    /// string, which would be a second way for the supplier to pick the type.
    /// </para>
    /// </remarks>
    private static JsonNode? ToJsonNode(string value, JsonValueType valueType)
    {
        if (valueType != JsonValueType.Json)
        {
            return JsonValue.Create(value);
        }

        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"json_edit: value_type is 'json' but the resolved value is not valid JSON " +
                $"({ex.Message}). Write 'value_type: string' if it is meant to be written " +
                $"as a string.",
                ex);
        }
    }
}
