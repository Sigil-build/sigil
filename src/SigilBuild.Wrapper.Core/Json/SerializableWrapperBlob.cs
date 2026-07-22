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

    /// <summary>
    /// The manifest's <c>App.Name</c> (T13). The <c>&lt;App.Name&gt;</c> segment of
    /// the default install dir (<c>&lt;scope root&gt;\&lt;App.Name&gt;</c>) and the
    /// value of the <c>{app.name}</c> token in an <c>install_dir</c> override.
    /// </summary>
    public string? AppName { get; init; }

    /// <summary>
    /// The manifest's optional <c>installer.install_dir</c> override template (T13);
    /// <c>null</c> when omitted so the default install dir applies. May reference
    /// <c>{scope_root}</c> / <c>{app.*}</c>; resolved at install time by the engine.
    /// </summary>
    public string? InstallDir { get; init; }

    // --- Signing (T11 / decision 7). ---

    /// <summary>
    /// True iff the manifest declared a verified <c>sign</c> block — i.e. the
    /// artifact is INTENDED to be Authenticode-signed. Set by the packager, never
    /// derived from <c>App.publisher</c> alone. The runtime gates the "Signed by
    /// {publisher}" trust line on <c>SignDeclared &amp;&amp; WinVerifyTrust(self) == valid</c>,
    /// so a tampered or re-stamped exe (signature invalid) drops the line even when
    /// this flag is set. A host-rendering concern, delivered like the brand fields
    /// (side-channel via the blob, not carried on the in-memory <see cref="WrapperBlob"/>).
    /// </summary>
    public bool SignDeclared { get; init; }

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

    /// <summary>
    /// Embedded license text (plain text / RTF-as-text v1), tag -&gt; file
    /// contents (P9, gap G10). <c>null</c> when no readable entry survived pack
    /// time (T14's original "no License screen" case). Each file is read at PACK
    /// time (<c>ExeWrapperPackager.ReadLicenseText</c>) — this carries contents,
    /// not paths. <c>Dictionary&lt;string,string&gt;</c> is already registered in
    /// <see cref="WrapperBlobJsonContext"/>, so no new source-gen entry is needed.
    /// </summary>
    public Dictionary<string, string>? LicenseText { get; init; }

    /// <summary>Declared custom wizard screens (T9).</summary>
    public SerializableInstallerScreen[] Screens { get; init; }
        = Array.Empty<SerializableInstallerScreen>();

    /// <summary>
    /// The ENABLED built-in option components (T8). Carried so the runtime can seed
    /// <c>option.*</c> for step gating and the host can render one checkbox each.
    /// </summary>
    public SerializableOptionComponent[] Options { get; init; }
        = Array.Empty<SerializableOptionComponent>();

    /// <summary>
    /// Declarative variables from <c>installer.vars</c> (P1), in manifest
    /// declaration order. The runtime evaluates each once at session start and
    /// seeds <c>var.&lt;Name&gt;</c>. An ordered array (not a map) so the wire form
    /// is deterministic and dependency order is reproducible.
    /// </summary>
    public SerializableVar[] Vars { get; init; } = Array.Empty<SerializableVar>();

    // --- P2 lifecycle hooks (gap G2). Ordered step lists that run OUTSIDE the
    //     rollback journal, around the transactional body. ---
    public SerializableInstallStep[] HookPreInstall    { get; init; } = Array.Empty<SerializableInstallStep>();
    public SerializableInstallStep[] HookPostInstall   { get; init; } = Array.Empty<SerializableInstallStep>();
    public SerializableInstallStep[] HookPreUninstall  { get; init; } = Array.Empty<SerializableInstallStep>();
    public SerializableInstallStep[] HookPostUninstall { get; init; } = Array.Empty<SerializableInstallStep>();

    // --- P2 run-after-install (gap G4): the Done-screen "Launch <App>" target. ---
    public string? RunAfterInstallPath { get; init; }
    public string[]? RunAfterInstallArgs { get; init; }

    /// <summary>
    /// First-class prerequisite units (P5, gap G6) from <c>installer.prerequisites</c>,
    /// in declaration order. Run before the journaled body (detect → install → re-detect).
    /// An ordered array so the wire form is deterministic.
    /// </summary>
    public SerializablePrerequisite[] Prerequisites { get; init; } = Array.Empty<SerializablePrerequisite>();

    /// <summary>P6 (gap G7): declared app mutex names probed before touching the install dir.</summary>
    public string[]? AppMutex { get; init; }

    /// <summary>
    /// P9 (gap G10): the manifest's optional <c>installer.language</c> fixed
    /// language tag. <c>null</c> when the manifest doesn't fix a language, so the
    /// session's language resolver falls through to <c>/lang</c> / the OS
    /// preference list / <c>en</c>. A host-rendering / session-bootstrap concern
    /// like <see cref="Screens"/> and <see cref="LicenseText"/> — not carried on
    /// the in-memory <see cref="WrapperBlob"/>.
    /// </summary>
    public string? Language { get; init; }

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
            Options:      ConvertOptions(s.Options),
            Vars:         ConvertVars(s.Vars),
            AppName:      s.AppName,
            InstallDir:   s.InstallDir,
            // T10: real ARP fields threaded into the in-memory blob so
            // InstallSession.PersistCompletion registers the actual
            // name/version/publisher/size instead of the placeholders.
            DisplayName:        s.DisplayName,
            Publisher:          s.Publisher,
            Version:            s.Version,
            EstimatedSizeBytes: s.EstimatedSizeBytes ?? 0,
            // P2: hooks + launch target.
            HookPreInstall:      ConvertSteps(s.HookPreInstall),
            HookPostInstall:     ConvertSteps(s.HookPostInstall),
            HookPreUninstall:    ConvertSteps(s.HookPreUninstall),
            HookPostUninstall:   ConvertSteps(s.HookPostUninstall),
            RunAfterInstallPath: s.RunAfterInstallPath,
            RunAfterInstallArgs: s.RunAfterInstallArgs,
            // P5: prerequisite units.
            Prerequisites: ConvertPrerequisites(s.Prerequisites),
            AppMutex: s.AppMutex);
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
            Vars         = SerializeVars(blob.Vars),
            AppName      = blob.AppName,
            InstallDir   = blob.InstallDir,
            // T10: carry the real ARP fields onto the wire DTO. A zero size is
            // emitted as null so a blob with no computed footprint round-trips to
            // the same "unset" state (matching the DisplayName/Version/Publisher nulls).
            DisplayName        = blob.DisplayName,
            Publisher          = blob.Publisher,
            Version            = blob.Version,
            EstimatedSizeBytes = blob.EstimatedSizeBytes == 0 ? null : blob.EstimatedSizeBytes,
            // P2: hooks + launch target.
            HookPreInstall      = SerializeSteps(blob.HookPreInstall ?? Array.Empty<InstallStep>()),
            HookPostInstall     = SerializeSteps(blob.HookPostInstall ?? Array.Empty<InstallStep>()),
            HookPreUninstall    = SerializeSteps(blob.HookPreUninstall ?? Array.Empty<InstallStep>()),
            HookPostUninstall   = SerializeSteps(blob.HookPostUninstall ?? Array.Empty<InstallStep>()),
            RunAfterInstallPath = blob.RunAfterInstallPath,
            RunAfterInstallArgs = blob.RunAfterInstallArgs is null ? null : ToStringArray(blob.RunAfterInstallArgs),
            // P5: prerequisite units.
            Prerequisites = SerializePrerequisites(blob.Prerequisites),
            AppMutex = blob.AppMutex is null ? null : ToStringArray(blob.AppMutex),
        };
    }

    private static string[] ToStringArray(IReadOnlyList<string> list)
    {
        if (list is string[] arr) return arr;
        var copy = new string[list.Count];
        for (var i = 0; i < list.Count; i++) copy[i] = list[i];
        return copy;
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

    private static InstallerVar[] ConvertVars(SerializableVar[] flat)
    {
        if (flat.Length == 0) return Array.Empty<InstallerVar>();
        var result = new InstallerVar[flat.Length];
        for (var i = 0; i < flat.Length; i++)
        {
            result[i] = SerializableVar.ToVar(flat[i]);
        }
        return result;
    }

    private static SerializableVar[] SerializeVars(IReadOnlyList<InstallerVar>? vars)
    {
        if (vars is null || vars.Count == 0) return Array.Empty<SerializableVar>();
        var result = new SerializableVar[vars.Count];
        for (var i = 0; i < vars.Count; i++)
        {
            result[i] = SerializableVar.FromVar(vars[i]);
        }
        return result;
    }

    private static InstallerPrerequisite[] ConvertPrerequisites(SerializablePrerequisite[] flat)
    {
        if (flat.Length == 0) return Array.Empty<InstallerPrerequisite>();
        var result = new InstallerPrerequisite[flat.Length];
        for (var i = 0; i < flat.Length; i++)
        {
            result[i] = SerializablePrerequisite.ToPrerequisite(flat[i]);
        }
        return result;
    }

    private static SerializablePrerequisite[] SerializePrerequisites(IReadOnlyList<InstallerPrerequisite>? prereqs)
    {
        if (prereqs is null || prereqs.Count == 0) return Array.Empty<SerializablePrerequisite>();
        var result = new SerializablePrerequisite[prereqs.Count];
        for (var i = 0; i < prereqs.Count; i++)
        {
            result[i] = SerializablePrerequisite.FromPrerequisite(prereqs[i]);
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
    public Dictionary<string, string>? Description { get; init; }
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
            Description: s.Description is null ? null : new LocalizedText(s.Description),
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
            Description = def.Description is null ? null : new Dictionary<string, string>(def.Description.Values),
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
    public Dictionary<string, string> Title { get; init; } = new();
    public Dictionary<string, string>? Subtitle { get; init; }
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
            Title: new LocalizedText(s.Title),
            Subtitle: s.Subtitle is null ? null : new LocalizedText(s.Subtitle),
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
            Title = new Dictionary<string, string>(screen.Title.Values),
            Subtitle = screen.Subtitle is null ? null : new Dictionary<string, string>(screen.Subtitle.Values),
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
/// Flat, AOT-friendly wire DTO for a single declarative variable (P1). Mirrors
/// <see cref="InstallerVar"/> so the source-generated context can serialize it
/// without reflection.
/// </summary>
internal sealed record SerializableVar
{
    public string Name { get; init; } = string.Empty;
    public string Expression { get; init; } = string.Empty;

    public static InstallerVar ToVar(SerializableVar s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new InstallerVar(s.Name, s.Expression);
    }

    public static SerializableVar FromVar(InstallerVar v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return new SerializableVar { Name = v.Name, Expression = v.Expression };
    }
}

/// <summary>
/// Flat, AOT-friendly wire DTO for a single prerequisite unit (P5, gap G6). Mirrors
/// <see cref="InstallerPrerequisite"/> so the source-generated context can serialize
/// it without reflection.
/// </summary>
internal sealed record SerializablePrerequisite
{
    public string Name { get; init; } = string.Empty;
    public string Detect { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? Sha256 { get; init; }
    public string[]? Args { get; init; }
    public int[]? ExitCodesOk { get; init; }
    public string? ScopeRequired { get; init; }
    public int? TimeoutSeconds { get; init; }

    public static InstallerPrerequisite ToPrerequisite(SerializablePrerequisite s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new InstallerPrerequisite(
            Name: s.Name,
            Detect: s.Detect,
            Source: s.Source,
            Sha256: s.Sha256,
            Args: s.Args,
            ExitCodesOk: s.ExitCodesOk,
            ScopeRequired: s.ScopeRequired,
            TimeoutSeconds: s.TimeoutSeconds);
    }

    public static SerializablePrerequisite FromPrerequisite(InstallerPrerequisite p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new SerializablePrerequisite
        {
            Name = p.Name,
            Detect = p.Detect,
            Source = p.Source,
            Sha256 = p.Sha256,
            Args = p.Args is null ? null : System.Linq.Enumerable.ToArray(p.Args),
            ExitCodesOk = p.ExitCodesOk is null ? null : System.Linq.Enumerable.ToArray(p.ExitCodesOk),
            ScopeRequired = p.ScopeRequired,
            TimeoutSeconds = p.TimeoutSeconds,
        };
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

    // P10 (gap G11): app-defined custom components. Custom marks the entry as one
    // (built-ins leave it false); Label/Description carry the localizable captions
    // (tag -> text); When is the optional applicability gate. All default to
    // absent, so a built-in component round-trips to the same three-field shape.
    public bool Custom { get; init; }
    public Dictionary<string, string>? Label { get; init; }
    public Dictionary<string, string>? Description { get; init; }
    public string? When { get; init; }

    public static InstallerOptionComponent ToComponent(SerializableOptionComponent s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new InstallerOptionComponent(
            s.Name, s.Default, s.Locked, s.Custom,
            s.Label is null ? null : new LocalizedText(s.Label),
            s.Description is null ? null : new LocalizedText(s.Description),
            s.When);
    }

    public static SerializableOptionComponent FromComponent(InstallerOptionComponent c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new SerializableOptionComponent
        {
            Name = c.Name,
            Default = c.Default,
            Locked = c.Locked,
            Custom = c.Custom,
            Label = c.Label is null ? null : new Dictionary<string, string>(c.Label.Values),
            Description = c.Description is null ? null : new Dictionary<string, string>(c.Description.Values),
            When = c.When,
        };
    }
}
