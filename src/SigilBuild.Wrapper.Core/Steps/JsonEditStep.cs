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
            current => JsonEditor.Set(current, pointer, value));

        return Task.FromResult(result);
    }
}

internal static class JsonEditor
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string Set(string? content, string pointer, string value)
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
        SetFinal(node, tokens[^1], ToJsonNode(value));

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

    // Interpret the resolved value as a JSON literal (number / bool / null / quoted
    // string / array / object) when it parses, else as a plain string.
    private static JsonNode? ToJsonNode(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }
}
