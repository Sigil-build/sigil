using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SigilBuild.Installer.Host.Branding;

/// <summary>
/// Mirrors <c>InstallTimeParameters.g.json</c> — the install-time parameter
/// contract the wrapper bundles alongside the wizard at pack time. Used by
/// <see cref="ViewModels.InstallerViewModel"/> to populate the Install Options
/// screen with the manifest's declared defaults, and by the install subprocess
/// launcher to translate user overrides into the wrapper's <c>/Name=value</c>
/// CLI form.
/// </summary>
public sealed class InstallTimeParameter
{
    /// <summary>Canonical parameter name as declared in <c>sigil.yaml</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>Scalar type: <c>string</c>, <c>path</c>, <c>bool</c>, <c>int</c>, <c>enum</c>, <c>secret</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";

    [JsonPropertyName("installTime")]
    public bool InstallTime { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Enum allowed values when <see cref="Type"/> is <c>enum</c>.</summary>
    [JsonPropertyName("values")]
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>
    /// Optional dynamic options source. When present, the wizard fetches the
    /// URL at install time, parses the JSON, and populates the parameter's
    /// ComboBox from the returned items.
    /// </summary>
    [JsonPropertyName("source")]
    public InstallTimeParameterSource? Source { get; init; }

    /// <summary>Default as JSON — could be a string, bool, or int per the manifest schema.</summary>
    [JsonPropertyName("default")]
    public JsonElement Default { get; init; }

    /// <summary>Stringified view of the default — convenient for one-line binding to TextBox.Text.</summary>
    public string DefaultAsString =>
        Default.ValueKind switch
        {
            JsonValueKind.String => Default.GetString() ?? "",
            JsonValueKind.True or JsonValueKind.False => Default.GetBoolean().ToString(),
            JsonValueKind.Number => Default.ToString(),
            JsonValueKind.Undefined or JsonValueKind.Null => "",
            _ => Default.ToString(),
        };
}

/// <summary>
/// Dynamic options source wire DTO. Mirrors <c>ParameterSource</c> in Core —
/// the wizard fetches <see cref="Url"/> at install time, parses the JSON, and
/// populates a ComboBox with the items at <see cref="ItemsPath"/> keyed by
/// <see cref="ValueProperty"/> / labelled by <see cref="LabelProperty"/>.
/// </summary>
public sealed class InstallTimeParameterSource
{
    [JsonPropertyName("url")]            public string Url { get; init; } = "";
    [JsonPropertyName("itemsPath")]      public string ItemsPath { get; init; } = "";
    [JsonPropertyName("valueProperty")]  public string ValueProperty { get; init; } = "";
    [JsonPropertyName("labelProperty")]  public string LabelProperty { get; init; } = "";
}

/// <summary>
/// Loads the install-time parameter list bundled into the wizard's working
/// directory. Returns an empty list when the file isn't present (e.g. older
/// setup.exe that hasn't been re-packed since the bundling code was added).
/// </summary>
public static class InstallTimeParameterLoader
{
    public static IReadOnlyList<InstallTimeParameter> LoadOrEmpty(string sideloadPath)
    {
        if (!File.Exists(sideloadPath)) return Array.Empty<InstallTimeParameter>();
        try
        {
            var json = File.ReadAllText(sideloadPath);
            var arr = JsonSerializer.Deserialize(json, InstallTimeParameterJsonContext.Default.InstallTimeParameterArray);
            return arr ?? Array.Empty<InstallTimeParameter>();
        }
        catch (JsonException)
        {
            return Array.Empty<InstallTimeParameter>();
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InstallTimeParameter[]))]
[JsonSerializable(typeof(InstallTimeParameterSource))]
internal sealed partial class InstallTimeParameterJsonContext : JsonSerializerContext { }
