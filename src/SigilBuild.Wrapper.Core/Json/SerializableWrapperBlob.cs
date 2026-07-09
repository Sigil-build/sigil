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

    public SerializableParameterDefinition[] Parameters { get; init; }
        = Array.Empty<SerializableParameterDefinition>();

    public SerializableInstallStep[] InstallSteps { get; init; } = Array.Empty<SerializableInstallStep>();
    public SerializableInstallStep[] PreInstall   { get; init; } = Array.Empty<SerializableInstallStep>();
    public SerializableInstallStep[] PostInstall  { get; init; } = Array.Empty<SerializableInstallStep>();
    public SerializableInstallStep[] UpdateSteps  { get; init; } = Array.Empty<SerializableInstallStep>();

    // --- Add/Remove Programs metadata (T10). Sourced from manifest.App.* +
    //     the packed size; consumed by ArpRegistration at install time. ---
    public string? DisplayName { get; init; }
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public long? EstimatedSizeBytes { get; init; }

    /// <summary>Resolved install scope (T12). Defaults to <see cref="InstallScope.Auto"/>.</summary>
    public InstallScope Scope { get; init; } = InstallScope.Auto;

    // --- Branding (T7). Derived at pack time (Avalonia cannot color-mix at
    //     runtime), delivered inside the blob rather than a sidecar file. ---

    /// <summary>Derived light-mode brand token map (token name → value).</summary>
    public Dictionary<string, string>? BrandTokensLight { get; init; }

    /// <summary>Derived dark-mode brand token map (token name → value).</summary>
    public Dictionary<string, string>? BrandTokensDark { get; init; }

    /// <summary>Base64-encoded brand logo image bytes, if any.</summary>
    public string? LogoBase64 { get; init; }

    /// <summary>Base64-encoded brand hero image bytes, if any.</summary>
    public string? HeroBase64 { get; init; }

    /// <summary>Embedded license text (plain text / RTF-as-text v1), if any (T14).</summary>
    public string? LicenseText { get; init; }

    /// <summary>Declared custom wizard screens (T9).</summary>
    public SerializableInstallerScreen[] Screens { get; init; }
        = Array.Empty<SerializableInstallerScreen>();

    /// <summary>
    /// The ENABLED built-in option components (T8). Carried so the runtime can seed
    /// <c>option.*</c> for step gating and the host can render one checkbox each.
    /// </summary>
    public SerializableOptionComponent[] Options { get; init; }
        = Array.Empty<SerializableOptionComponent>();

    public static WrapperBlob ToWrapperBlob(SerializableWrapperBlob s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new WrapperBlob(
            AppId: s.AppId,
            Parameters: ConvertParameters(s.Parameters),
            InstallSteps: ConvertSteps(s.InstallSteps),
            PreInstall:   ConvertSteps(s.PreInstall),
            PostInstall:  ConvertSteps(s.PostInstall),
            UpdateSteps:  ConvertSteps(s.UpdateSteps),
            Scope:        s.Scope,
            Options:      ConvertOptions(s.Options));
    }

    public static SerializableWrapperBlob FromWrapperBlob(WrapperBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        return new SerializableWrapperBlob
        {
            AppId = blob.AppId,
            Parameters = SerializeParameters(blob.Parameters),
            InstallSteps = SerializeSteps(blob.InstallSteps),
            PreInstall   = SerializeSteps(blob.PreInstall),
            PostInstall  = SerializeSteps(blob.PostInstall),
            UpdateSteps  = SerializeSteps(blob.UpdateSteps),
            Scope        = blob.Scope,
            Options      = SerializeOptions(blob.Options),
        };
    }

    private static InstallerOptionComponent[] ConvertOptions(SerializableOptionComponent[] flat)
    {
        if (flat.Length == 0) return Array.Empty<InstallerOptionComponent>();
        var result = new InstallerOptionComponent[flat.Length];
        for (var i = 0; i < flat.Length; i++)
        {
            result[i] = SerializableOptionComponent.ToComponent(flat[i]);
        }
        return result;
    }

    private static SerializableOptionComponent[] SerializeOptions(IReadOnlyList<InstallerOptionComponent>? options)
    {
        if (options is null || options.Count == 0) return Array.Empty<SerializableOptionComponent>();
        var result = new SerializableOptionComponent[options.Count];
        for (var i = 0; i < options.Count; i++)
        {
            result[i] = SerializableOptionComponent.FromComponent(options[i]);
        }
        return result;
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

/// <summary>
/// Flat, AOT-friendly wire DTO for a declared custom wizard screen (T9).
/// Mirrors <see cref="InstallerScreen"/> with an array of
/// <see cref="SerializableScreenField"/> so the source-generated context can
/// serialize it without reflection.
/// </summary>
internal sealed record SerializableInstallerScreen
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? When { get; init; }
    public SerializableScreenField[] Fields { get; init; } = Array.Empty<SerializableScreenField>();

    public static InstallerScreen ToInstallerScreen(SerializableInstallerScreen s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var fields = new ScreenField[s.Fields.Length];
        for (var i = 0; i < s.Fields.Length; i++)
        {
            fields[i] = SerializableScreenField.ToScreenField(s.Fields[i]);
        }

        return new InstallerScreen(
            Id: s.Id,
            Title: s.Title,
            Subtitle: s.Subtitle,
            When: s.When,
            Fields: fields);
    }

    public static SerializableInstallerScreen FromInstallerScreen(InstallerScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var fields = new SerializableScreenField[screen.Fields.Count];
        for (var i = 0; i < screen.Fields.Count; i++)
        {
            fields[i] = SerializableScreenField.FromScreenField(screen.Fields[i]);
        }

        return new SerializableInstallerScreen
        {
            Id = screen.Id,
            Title = screen.Title,
            Subtitle = screen.Subtitle,
            When = screen.When,
            Fields = fields,
        };
    }
}

/// <summary>
/// Flat wire DTO for a single <see cref="ScreenField"/> on a
/// <see cref="SerializableInstallerScreen"/>.
/// </summary>
internal sealed record SerializableScreenField
{
    public string Param { get; init; } = string.Empty;
    public string? Widget { get; init; }

    public static ScreenField ToScreenField(SerializableScreenField s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new ScreenField(s.Param, s.Widget);
    }

    public static SerializableScreenField FromScreenField(ScreenField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return new SerializableScreenField { Param = field.Param, Widget = field.Widget };
    }
}

/// <summary>
/// Flat, AOT-friendly wire DTO for a single ENABLED built-in option component
/// (T8). Mirrors <see cref="InstallerOptionComponent"/> so the source-generated
/// context can serialize it without reflection.
/// </summary>
internal sealed record SerializableOptionComponent
{
    public string Name { get; init; } = string.Empty;
    public bool Default { get; init; }
    public bool Locked { get; init; }

    public static InstallerOptionComponent ToComponent(SerializableOptionComponent s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new InstallerOptionComponent(s.Name, s.Default, s.Locked);
    }

    public static SerializableOptionComponent FromComponent(InstallerOptionComponent c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new SerializableOptionComponent { Name = c.Name, Default = c.Default, Locked = c.Locked };
    }
}
