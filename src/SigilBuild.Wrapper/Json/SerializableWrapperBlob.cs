using System;
using System.Collections.Generic;
using System.Text.Json;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper.Json;

/// <summary>
/// Top-level wire DTO for the embedded <c>SIGIL_BLOB_V1</c> resource.
/// Round-trips into <see cref="WrapperBlob"/> via <see cref="ToWrapperBlob"/>
/// and back via <see cref="FromWrapperBlob"/>.
/// </summary>
internal sealed record SerializableWrapperBlob
{
    public string AppId { get; init; } = "<unset>";

    // App metadata for install-time template substitution
    // (${app.name}, ${app.version}, etc.). Default values keep older blobs
    // (built before this field existed) deserializable.
    public string AppName { get; init; } = "<unset>";
    public string AppVersion { get; init; } = "0.0.0";
    public string AppPublisher { get; init; } = "<unset>";
    public string? AppDescription { get; init; }
    public string? AppHomepage { get; init; }

    public SerializableParameterDefinition[] Parameters { get; init; }
        = Array.Empty<SerializableParameterDefinition>();

    public SerializableInstallStep[] InstallSteps { get; init; } = Array.Empty<SerializableInstallStep>();
    public SerializableInstallStep[] PreInstall   { get; init; } = Array.Empty<SerializableInstallStep>();
    public SerializableInstallStep[] PostInstall  { get; init; } = Array.Empty<SerializableInstallStep>();
    public SerializableInstallStep[] UpdateSteps  { get; init; } = Array.Empty<SerializableInstallStep>();
    public SerializableInstallStep[] Uninstall    { get; init; } = Array.Empty<SerializableInstallStep>();

    /// <summary>
    /// When <c>true</c>, the wrapper runtime coerces its mode to
    /// <c>Uninstall</c> on entry — used by the dedicated <c>uninstaller.exe</c>
    /// emitted alongside <c>setup.exe</c> so end-users double-clicking it land
    /// in the uninstall flow without needing to pass <c>/Uninstall</c>.
    /// Default <c>false</c> keeps blobs built before this field existed
    /// behaving as installer-mode.
    /// </summary>
    public bool IsUninstaller { get; init; }

    public static WrapperBlob ToWrapperBlob(SerializableWrapperBlob s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new WrapperBlob(
            AppId: s.AppId,
            App: new AppMetadata(
                Id: s.AppId,
                Name: s.AppName,
                Version: s.AppVersion,
                Publisher: s.AppPublisher,
                Description: s.AppDescription,
                Homepage: s.AppHomepage),
            Parameters: ConvertParameters(s.Parameters),
            InstallSteps: ConvertSteps(s.InstallSteps),
            PreInstall:   ConvertSteps(s.PreInstall),
            PostInstall:  ConvertSteps(s.PostInstall),
            UpdateSteps:  ConvertSteps(s.UpdateSteps),
            Uninstall:    ConvertSteps(s.Uninstall),
            IsUninstaller: s.IsUninstaller);
    }

    public static SerializableWrapperBlob FromWrapperBlob(WrapperBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        return new SerializableWrapperBlob
        {
            AppId = blob.AppId,
            AppName = blob.App.Name,
            AppVersion = blob.App.Version,
            AppPublisher = blob.App.Publisher,
            AppDescription = blob.App.Description,
            AppHomepage = blob.App.Homepage,
            Parameters = SerializeParameters(blob.Parameters),
            InstallSteps = SerializeSteps(blob.InstallSteps),
            PreInstall   = SerializeSteps(blob.PreInstall),
            PostInstall  = SerializeSteps(blob.PostInstall),
            UpdateSteps  = SerializeSteps(blob.UpdateSteps),
            Uninstall    = SerializeSteps(blob.Uninstall),
            IsUninstaller = blob.IsUninstaller,
        };
    }

    private static InstallStep[] ConvertSteps(SerializableInstallStep[] flat)
    {
        if (flat.Length == 0) return Array.Empty<InstallStep>();
        var result = new InstallStep[flat.Length];
        for (var i = 0; i < flat.Length; i++)
        {
            result[i] = SerializableInstallStepConverter.ToInstallStep(flat[i]);
        }
        return result;
    }

    private static SerializableInstallStep[] SerializeSteps(IReadOnlyList<InstallStep> steps)
    {
        if (steps.Count == 0) return Array.Empty<SerializableInstallStep>();
        var result = new SerializableInstallStep[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            result[i] = SerializableInstallStepConverter.FromInstallStep(steps[i]);
        }
        return result;
    }

    private static ParameterDefinition[] ConvertParameters(SerializableParameterDefinition[] flat)
    {
        if (flat.Length == 0) return Array.Empty<ParameterDefinition>();
        var result = new ParameterDefinition[flat.Length];
        for (var i = 0; i < flat.Length; i++)
        {
            result[i] = SerializableParameterDefinition.ToParameterDefinition(flat[i]);
        }
        return result;
    }

    private static SerializableParameterDefinition[] SerializeParameters(IReadOnlyList<ParameterDefinition> defs)
    {
        if (defs.Count == 0) return Array.Empty<SerializableParameterDefinition>();
        var result = new SerializableParameterDefinition[defs.Count];
        for (var i = 0; i < defs.Count; i++)
        {
            result[i] = SerializableParameterDefinition.FromParameterDefinition(defs[i]);
        }
        return result;
    }
}

/// <summary>
/// Wire DTO for a parameter definition. Mirrors
/// <see cref="ParameterDefinition"/> but replaces the <c>object?</c>
/// <see cref="ParameterDefinition.Default"/> field with a
/// <see cref="JsonElement"/> so the source-generated JSON context can
/// serialize it without reflection.
/// </summary>
internal sealed record SerializableParameterDefinition
{
    public string Name { get; init; } = string.Empty;
    public ParameterType Type { get; init; } = ParameterType.String;
    public JsonElement? Default { get; init; }
    public string[]? EnumValues { get; init; }
    public bool InstallTime { get; init; }
    public string? Description { get; init; }
    public string? Pattern { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }

    public static ParameterDefinition ToParameterDefinition(SerializableParameterDefinition s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new ParameterDefinition(
            Name: s.Name,
            Type: s.Type,
            Default: JsonElementToObject(s.Default, s.Type),
            EnumValues: s.EnumValues,
            InstallTime: s.InstallTime,
            Description: s.Description,
            Pattern: s.Pattern,
            Min: s.Min,
            Max: s.Max);
    }

    public static SerializableParameterDefinition FromParameterDefinition(ParameterDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return new SerializableParameterDefinition
        {
            Name = def.Name,
            Type = def.Type,
            Default = ObjectToJsonElement(def.Default),
            EnumValues = ToArray(def.EnumValues),
            InstallTime = def.InstallTime,
            Description = def.Description,
            Pattern = def.Pattern,
            Min = def.Min,
            Max = def.Max,
        };
    }

    private static T[]? ToArray<T>(IReadOnlyList<T>? list)
    {
        if (list is null) return null;
        if (list is T[] arr) return arr;
        var copy = new T[list.Count];
        for (var i = 0; i < list.Count; i++) copy[i] = list[i];
        return copy;
    }

    private static object? JsonElementToObject(JsonElement? value, ParameterType type)
    {
        if (value is null) return null;
        var v = value.Value;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => type == ParameterType.Int && v.TryGetInt32(out var i)
                ? i
                : (v.TryGetInt64(out var l) ? l : v.GetDouble()),
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            JsonValueKind.Null   => null,
            _                    => v,
        };
    }

    private static JsonElement? ObjectToJsonElement(object? value)
    {
        if (value is null) return null;

        string json = value switch
        {
            string s => System.Text.Json.JsonSerializer.Serialize(s, WrapperBlobJsonContext.Default.String),
            bool b   => b ? "true" : "false",
            int i    => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l   => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonElement je => je.GetRawText(),
            _ => System.Text.Json.JsonSerializer.Serialize(
                     value.ToString() ?? string.Empty,
                     WrapperBlobJsonContext.Default.String),
        };

        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
