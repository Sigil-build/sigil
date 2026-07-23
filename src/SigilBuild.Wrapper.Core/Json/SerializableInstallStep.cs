using System;
using System.Collections.Generic;
using System.Text.Json;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Wrapper.Json;

/// <summary>
/// Flat, AOT-friendly DTO for a single <see cref="InstallStep"/> traversing
/// the JSON wire boundary between the pack-time blob writer and the
/// install-time runtime. The discriminated-union shape of
/// <see cref="InstallStep"/> would otherwise require a
/// <c>JsonDerivedType</c> setup that pulls a schema-evolution dependency
/// into <c>SigilBuild.Core</c>; flattening here keeps the wire schema in
/// the wrapper assembly and lets <see cref="InstallStep"/> evolve freely.
/// </summary>
/// <remarks>
/// All per-type fields are nullable optional. <see cref="Type"/> is the
/// step-kind discriminator (mirrors the YAML <c>type:</c> values:
/// <c>file_copy</c>, <c>directory_create</c>, etc.). When extending this
/// DTO for a new step type (Tasks 15–17) add the matching arms in
/// <see cref="SerializableInstallStepConverter.ToInstallStep"/> and
/// <see cref="SerializableInstallStepConverter.FromInstallStep"/>.
/// </remarks>
internal sealed record SerializableInstallStep
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? When { get; init; }
    public string OnFailure { get; init; } = "fail";

    // file_copy
    public string? From { get; init; }
    public string? To { get; init; }
    public bool? Overwrite { get; init; }

    // directory_create / file_delete / directory_delete
    public string? Path { get; init; }
    public string? IfMissing { get; init; }
    public bool? Recursive { get; init; }

    // registry_*
    public string? Hive { get; init; }
    public string? Key { get; init; }
    public string? Name { get; init; }

    /// <summary>
    /// Registry value-type discriminator (<c>REG_SZ</c>, <c>REG_DWORD</c>, …),
    /// renamed from <c>Type</c> on the wire to avoid clashing with the
    /// step-kind discriminator on the outer DTO.
    /// </summary>
    public string? RegistryType { get; init; }

    /// <summary>
    /// Registry value payload — wire-typed as <see cref="JsonElement"/> so
    /// strings, ints, bools, and byte arrays round-trip through the
    /// AOT-safe source-generated context without dragging in
    /// reflection-based polymorphism for <c>object</c>.
    /// </summary>
    public JsonElement? Value { get; init; }

    public string? View { get; init; }

    // shortcut_create
    public string? Target { get; init; }
    public string? Location { get; init; }

    /// <summary>
    /// Shortcut display name, renamed from <c>Name</c> on the wire to avoid
    /// clashing with the registry <c>Name</c> field already in this flat DTO.
    /// </summary>
    public string? ShortcutName { get; init; }

    public string[]? Args { get; init; }
    public string? WorkingDir { get; init; }
    public string? Icon { get; init; }
    public string? Description { get; init; }

    // env_set
    public string? EnvName { get; init; }
    public string? EnvValue { get; init; }
    public string? Scope { get; init; }
    public string? Action { get; init; }
    public string? Separator { get; init; }

    // run_program
    public string? Program { get; init; }
    public string[]? ProgramArgs { get; init; }
    public bool? Wait { get; init; }
    public string? Cwd { get; init; }
    public int[]? ExpectedExitCodes { get; init; }
    public int? TimeoutSeconds { get; init; }

    // http_download (P4). TimeoutSeconds above is shared with run_program.
    public string? HttpUrl { get; init; }
    public string? HttpDest { get; init; }
    public string? Sha256 { get; init; }
    public int? HttpRetries { get; init; }

    // ini_write / json_edit / xml_edit (P8). Path (above) is shared.
    public bool? CreateIfMissing { get; init; }
    public string? Section { get; init; }
    public string? IniKey { get; init; }
    public string? IniValue { get; init; }
    public string? Pointer { get; init; }
    public string? JsonEditValue { get; init; }
    public string? Xpath { get; init; }
    public string? Attribute { get; init; }
    public string? XmlValue { get; init; }

    // service_install
    public string? ServiceName { get; init; }
    public string? BinaryPath { get; init; }
    public string? DisplayName { get; init; }
    public string? ServiceDescription { get; init; }
    public string? StartType { get; init; }
    public string? ServiceAccount { get; init; }
    public bool? StartAfterInstall { get; init; }

    // scheduled_task_create (P11, T11.1). Program/ProgramArgs above belong to
    // run_program; the task's executable + args get their own fields since a
    // task also carries a Trigger/RunLevel that run_program has no concept of.
    public string? TaskName { get; init; }
    public string? TaskProgram { get; init; }
    public string? TaskArguments { get; init; }
    public string? TaskTrigger { get; init; }
    public string? TaskRunLevel { get; init; }
}

/// <summary>
/// Converters between <see cref="SerializableInstallStep"/> and the typed
/// <see cref="InstallStep"/> graph in <c>SigilBuild.Core</c>.
/// </summary>
internal static class SerializableInstallStepConverter
{
    public static InstallStep ToInstallStep(SerializableInstallStep s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var onFailure = ParseOnFailure(s.OnFailure);

        return s.Type switch
        {
            "file_copy" => new InstallStep.FileCopy(
                s.Id,
                s.From ?? throw MissingField("file_copy", "from", s.Id),
                s.To ?? throw MissingField("file_copy", "to", s.Id),
                s.Overwrite ?? true,
                s.When,
                onFailure),

            "directory_create" => new InstallStep.DirectoryCreate(
                s.Id,
                s.Path ?? throw MissingField("directory_create", "path", s.Id),
                s.When,
                onFailure),

            "file_delete" => new InstallStep.FileDelete(
                s.Id,
                s.Path ?? throw MissingField("file_delete", "path", s.Id),
                s.IfMissing ?? "fail",
                s.When,
                onFailure),

            "directory_delete" => new InstallStep.DirectoryDelete(
                s.Id,
                s.Path ?? throw MissingField("directory_delete", "path", s.Id),
                s.Recursive ?? false,
                s.When,
                onFailure),

            "registry_write" => new InstallStep.RegistryWrite(
                s.Id,
                s.Hive ?? throw MissingField("registry_write", "hive", s.Id),
                s.Key ?? throw MissingField("registry_write", "key", s.Id),
                s.Name ?? string.Empty,
                s.RegistryType ?? "REG_SZ",
                JsonElementToObject(s.Value),
                s.View ?? "default",
                s.When,
                onFailure),

            "registry_delete_value" => new InstallStep.RegistryDeleteValue(
                s.Id,
                s.Hive ?? throw MissingField("registry_delete_value", "hive", s.Id),
                s.Key ?? throw MissingField("registry_delete_value", "key", s.Id),
                s.Name ?? string.Empty,
                s.View ?? "default",
                s.When,
                onFailure),

            "registry_delete_key" => new InstallStep.RegistryDeleteKey(
                s.Id,
                s.Hive ?? throw MissingField("registry_delete_key", "hive", s.Id),
                s.Key ?? throw MissingField("registry_delete_key", "key", s.Id),
                s.Recursive ?? false,
                s.View ?? "default",
                s.When,
                onFailure),

            "shortcut_create" => new InstallStep.ShortcutCreate(
                s.Id,
                s.Target ?? throw MissingField("shortcut_create", "target", s.Id),
                s.Location ?? throw MissingField("shortcut_create", "location", s.Id),
                s.ShortcutName ?? throw MissingField("shortcut_create", "name", s.Id),
                s.Args,
                s.WorkingDir,
                s.Icon,
                s.Description,
                s.When,
                onFailure),

            "env_set" => new InstallStep.EnvSet(
                s.Id,
                s.EnvName ?? throw MissingField("env_set", "name", s.Id),
                s.EnvValue ?? string.Empty,
                s.Scope ?? "user",
                s.Action ?? "set",
                s.Separator ?? ";",
                s.When,
                onFailure),

            "run_program" => new InstallStep.RunProgram(
                s.Id,
                s.Program ?? throw MissingField("run_program", "program", s.Id),
                s.ProgramArgs,
                s.Wait ?? true,
                s.Cwd,
                s.ExpectedExitCodes,
                s.TimeoutSeconds,
                s.When,
                onFailure),

            "http_download" => new InstallStep.HttpDownload(
                s.Id,
                s.HttpUrl ?? throw MissingField("http_download", "url", s.Id),
                s.HttpDest ?? throw MissingField("http_download", "dest", s.Id),
                s.Sha256 ?? throw MissingField("http_download", "sha256", s.Id),
                s.TimeoutSeconds,
                s.HttpRetries,
                s.When,
                onFailure),

            "ini_write" => new InstallStep.IniWrite(
                s.Id,
                s.Path ?? throw MissingField("ini_write", "path", s.Id),
                s.Section ?? string.Empty,
                s.IniKey ?? throw MissingField("ini_write", "key", s.Id),
                s.IniValue ?? string.Empty,
                s.CreateIfMissing ?? false,
                s.When,
                onFailure),

            "json_edit" => new InstallStep.JsonEdit(
                s.Id,
                s.Path ?? throw MissingField("json_edit", "path", s.Id),
                s.Pointer ?? throw MissingField("json_edit", "pointer", s.Id),
                s.JsonEditValue ?? string.Empty,
                s.CreateIfMissing ?? false,
                s.When,
                onFailure),

            "xml_edit" => new InstallStep.XmlEdit(
                s.Id,
                s.Path ?? throw MissingField("xml_edit", "path", s.Id),
                s.Xpath ?? throw MissingField("xml_edit", "xpath", s.Id),
                s.Attribute,
                s.XmlValue ?? string.Empty,
                s.CreateIfMissing ?? false,
                s.When,
                onFailure),

            "service_install" => new InstallStep.ServiceInstall(
                s.Id,
                s.ServiceName ?? throw MissingField("service_install", "name", s.Id),
                s.BinaryPath  ?? throw MissingField("service_install", "binary_path", s.Id),
                s.DisplayName ?? s.ServiceName ?? "",
                s.ServiceDescription,
                s.StartType ?? "auto",
                s.ServiceAccount ?? "LocalSystem",
                s.StartAfterInstall ?? true,
                s.When,
                onFailure),

            "scheduled_task_create" => new InstallStep.ScheduledTaskCreate(
                s.Id,
                s.TaskName ?? throw MissingField("scheduled_task_create", "name", s.Id),
                s.TaskProgram ?? throw MissingField("scheduled_task_create", "program", s.Id),
                s.TaskArguments,
                s.TaskTrigger ?? throw MissingField("scheduled_task_create", "trigger", s.Id),
                s.TaskRunLevel ?? "limited",
                s.When,
                onFailure),

            "com_register" => new InstallStep.ComRegister(
                s.Id,
                s.Path ?? throw MissingField("com_register", "path", s.Id),
                s.When,
                onFailure),

            _ => throw new InvalidOperationException(
                $"unknown step type '{s.Type}' for step '{s.Id}'"),
        };
    }

    public static SerializableInstallStep FromInstallStep(InstallStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var onFailure = FormatOnFailure(step.OnFailure);

        return step switch
        {
            InstallStep.FileCopy x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "file_copy",
                When = x.When,
                OnFailure = onFailure,
                From = x.From,
                To = x.To,
                Overwrite = x.Overwrite,
            },

            InstallStep.DirectoryCreate x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "directory_create",
                When = x.When,
                OnFailure = onFailure,
                Path = x.Path,
            },

            InstallStep.FileDelete x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "file_delete",
                When = x.When,
                OnFailure = onFailure,
                Path = x.Path,
                IfMissing = x.IfMissing,
            },

            InstallStep.DirectoryDelete x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "directory_delete",
                When = x.When,
                OnFailure = onFailure,
                Path = x.Path,
                Recursive = x.Recursive,
            },

            InstallStep.RegistryWrite x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "registry_write",
                When = x.When,
                OnFailure = onFailure,
                Hive = x.Hive,
                Key = x.Key,
                Name = x.Name,
                RegistryType = x.Type,
                Value = ObjectToJsonElement(x.Value),
                View = x.View,
            },

            InstallStep.RegistryDeleteValue x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "registry_delete_value",
                When = x.When,
                OnFailure = onFailure,
                Hive = x.Hive,
                Key = x.Key,
                Name = x.Name,
                View = x.View,
            },

            InstallStep.RegistryDeleteKey x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "registry_delete_key",
                When = x.When,
                OnFailure = onFailure,
                Hive = x.Hive,
                Key = x.Key,
                Recursive = x.Recursive,
                View = x.View,
            },

            InstallStep.ShortcutCreate x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "shortcut_create",
                When = x.When,
                OnFailure = onFailure,
                Target = x.Target,
                Location = x.Location,
                ShortcutName = x.Name,
                Args = ToArray(x.Args),
                WorkingDir = x.WorkingDir,
                Icon = x.Icon,
                Description = x.Description,
            },

            InstallStep.EnvSet x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "env_set",
                When = x.When,
                OnFailure = onFailure,
                EnvName = x.Name,
                EnvValue = x.Value,
                Scope = x.Scope,
                Action = x.Action,
                Separator = x.Separator,
            },

            InstallStep.RunProgram x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "run_program",
                When = x.When,
                OnFailure = onFailure,
                Program = x.Program,
                ProgramArgs = ToArray(x.Args),
                Wait = x.Wait,
                Cwd = x.Cwd,
                ExpectedExitCodes = ToArray(x.ExpectedExitCodes),
                TimeoutSeconds = x.TimeoutSeconds,
            },

            InstallStep.HttpDownload x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "http_download",
                When = x.When,
                OnFailure = onFailure,
                HttpUrl = x.Url,
                HttpDest = x.Dest,
                Sha256 = x.Sha256,
                TimeoutSeconds = x.TimeoutSeconds,
                HttpRetries = x.Retries,
            },

            InstallStep.IniWrite x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "ini_write",
                When = x.When,
                OnFailure = onFailure,
                Path = x.Path,
                Section = x.Section,
                IniKey = x.Key,
                IniValue = x.Value,
                CreateIfMissing = x.CreateIfMissing,
            },

            InstallStep.JsonEdit x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "json_edit",
                When = x.When,
                OnFailure = onFailure,
                Path = x.Path,
                Pointer = x.JsonPointer,
                JsonEditValue = x.Value,
                CreateIfMissing = x.CreateIfMissing,
            },

            InstallStep.XmlEdit x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "xml_edit",
                When = x.When,
                OnFailure = onFailure,
                Path = x.Path,
                Xpath = x.Xpath,
                Attribute = x.Attribute,
                XmlValue = x.Value,
                CreateIfMissing = x.CreateIfMissing,
            },

            InstallStep.ServiceInstall x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "service_install",
                When = x.When,
                OnFailure = onFailure,
                ServiceName = x.Name,
                BinaryPath = x.BinaryPath,
                DisplayName = x.DisplayName,
                ServiceDescription = x.Description,
                StartType = x.StartType,
                ServiceAccount = x.ServiceAccount,
                StartAfterInstall = x.StartAfterInstall,
            },

            InstallStep.ScheduledTaskCreate x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "scheduled_task_create",
                When = x.When,
                OnFailure = onFailure,
                TaskName = x.Name,
                TaskProgram = x.Program,
                TaskArguments = x.Arguments,
                TaskTrigger = x.Trigger,
                TaskRunLevel = x.RunLevel,
            },

            InstallStep.ComRegister x => new SerializableInstallStep
            {
                Id = x.Id,
                Type = "com_register",
                When = x.When,
                OnFailure = onFailure,
                Path = x.Path,
            },

            _ => throw new InvalidOperationException(
                $"unsupported InstallStep subtype: {step.GetType().Name}"),
        };
    }

    private static OnFailure ParseOnFailure(string raw) => raw switch
    {
        "rollback" => OnFailure.Rollback,
        "continue" => OnFailure.Continue,
        "fail"     => OnFailure.Fail,
        _          => OnFailure.Fail,
    };

    private static string FormatOnFailure(OnFailure value) => value switch
    {
        OnFailure.Rollback => "rollback",
        OnFailure.Continue => "continue",
        OnFailure.Fail     => "fail",
        _                  => "fail",
    };

    private static InvalidOperationException MissingField(string type, string field, string id) =>
        new($"step '{id}' of type '{type}' is missing required field '{field}'");

    private static T[]? ToArray<T>(IReadOnlyList<T>? list)
    {
        if (list is null) return null;
        if (list is T[] arr) return arr;
        var copy = new T[list.Count];
        for (var i = 0; i < list.Count; i++) copy[i] = list[i];
        return copy;
    }

    /// <summary>
    /// Convert a registry-write <see cref="JsonElement"/> back to the
    /// <c>object?</c> shape <see cref="InstallStep.RegistryWrite.Value"/>
    /// expects. We honour the four scalar shapes the Sprint 5a parser
    /// produces (<see cref="string"/>, <see cref="int"/>/<see cref="long"/>,
    /// <see cref="bool"/>) plus <c>null</c>; arrays/objects fall through to
    /// the raw <see cref="JsonElement"/> for forward-compatibility.
    /// </summary>
    private static object? JsonElementToObject(JsonElement? value)
    {
        if (value is null) return null;
        var v = value.Value;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            JsonValueKind.Null   => null,
            _                    => v,
        };
    }

    /// <summary>
    /// Wrap the parser-produced <c>object?</c> registry value into a
    /// <see cref="JsonElement"/>. AOT-safe because we only call
    /// <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> on
    /// strings we built ourselves with primitive serializers — no
    /// <c>JsonSerializer.Serialize&lt;T&gt;</c> on <c>object</c> graphs.
    /// </summary>
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
