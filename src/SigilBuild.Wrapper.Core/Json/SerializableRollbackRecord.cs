using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper.Json;

/// <summary>
/// Flat AOT-friendly DTO for the rollback journal. The <see cref="Type"/>
/// discriminator selects which concrete <see cref="RollbackRecord"/> to
/// reconstruct on read. The discriminated-union shape of
/// <see cref="RollbackRecord"/> would otherwise require a
/// <c>JsonDerivedType</c> setup that pulls reflective polymorphism into
/// the AOT-published wrapper runtime; flattening to one record keeps the
/// wire schema fixed and lets <see cref="RollbackRecord"/> evolve freely.
/// </summary>
/// <remarks>
/// The <see cref="PriorValue"/> slot is wire-typed as <see cref="JsonElement"/>
/// for the same reason as <see cref="SerializableInstallStep.Value"/>: AOT
/// JSON cannot serialize <c>object?</c> graphs without dragging in
/// reflection. Conversion happens at the seam in
/// <see cref="SerializableRollbackRecordExtensions"/>.
/// </remarks>
internal sealed record SerializableRollbackRecord
{
    /// <summary>Discriminator: <c>restore_file</c>, <c>remove_directory</c>,
    /// <c>restore_registry_value</c>, <c>restore_registry_key</c>,
    /// <c>delete_shortcut</c>, <c>restore_env</c>, <c>remove_uninstaller</c>,
    /// <c>remove_service</c>, <c>delete_scheduled_task</c>.</summary>
    public string Type { get; init; } = string.Empty;

    // RestoreFile / RemoveDirectory / DeleteShortcut all use Path.
    public string? Path { get; init; }

    // RestoreFile only.
    public bool? ExistedBefore { get; init; }
    public string? BackupPath { get; init; }

    // Registry-value / registry-key.
    public string? Hive { get; init; }
    public string? Key { get; init; }
    public string? Name { get; init; }
    public string? View { get; init; }

    // RestoreRegistryValue.
    public string? PriorTypeStr { get; init; }
    public JsonElement? PriorValue { get; init; }
    public bool? PreviouslyAbsent { get; init; }

    // RestoreRegistryKey.
    public SerializableRegistryValueAtKey[]? ValuesAtKeyLevel { get; init; }

    // RestoreEnv (Name is shared with the registry slot above).
    public string? Scope { get; init; }
    public string? PriorValueString { get; init; }

    // RestoreDeletedFile / RestoreDeletedDirectory: stash bookkeeping so the
    // uninstaller can copy the bytes back from a temp location captured at
    // pre-install time. OriginalPath is the file/dir we deleted; StashPath
    // is where its bytes (or recursive copy) were stashed.
    public string? OriginalPath { get; init; }
    public string? StashPath { get; init; }

    // RemoveService: name of the Windows service to sc stop + sc delete on
    // rollback / uninstall. Recorded by service_install BEFORE the create
    // so an interrupted install still tears the service down.
    public string? ServiceName { get; init; }

    // DeleteScheduledTask (P11, T11.1): name of the Scheduled Task to
    // schtasks /Delete on rollback / uninstall. Recorded by
    // scheduled_task_create BEFORE the create so an interrupted install
    // still tears the task down. Name only — no secrets.
    public string? TaskName { get; init; }
}

/// <summary>
/// Wire DTO for one captured registry value at the key level, used by the
/// <see cref="RollbackRecord.RestoreRegistryKey"/> snapshot.
/// </summary>
internal sealed record SerializableRegistryValueAtKey
{
    public string Name { get; init; } = string.Empty;
    public string TypeStr { get; init; } = "REG_SZ";
    public JsonElement? Value { get; init; }
}

/// <summary>
/// Conversion helpers between <see cref="RollbackRecord"/> and the flat
/// <see cref="SerializableRollbackRecord"/> wire DTO.
/// </summary>
internal static class SerializableRollbackRecordExtensions
{
    public static SerializableRollbackRecord ToSerializable(this RollbackRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record switch
        {
            RollbackRecord.RestoreFile r => new SerializableRollbackRecord
            {
                Type = "restore_file",
                Path = r.Path,
                ExistedBefore = r.ExistedBefore,
                BackupPath = r.BackupPath,
            },

            RollbackRecord.RemoveDirectory r => new SerializableRollbackRecord
            {
                Type = "remove_directory",
                Path = r.Path,
            },

            RollbackRecord.DeleteShortcut r => new SerializableRollbackRecord
            {
                Type = "delete_shortcut",
                Path = r.Path,
            },

            RollbackRecord.RemoveUninstaller r => new SerializableRollbackRecord
            {
                Type = "remove_uninstaller",
                Path = r.Path,
            },

            RollbackRecord.RestoreRegistryValue r => new SerializableRollbackRecord
            {
                Type = "restore_registry_value",
                Hive = r.Hive,
                Key = r.Key,
                Name = r.Name,
                View = r.View,
                PriorTypeStr = r.PriorTypeStr,
                PriorValue = ObjectToJsonElement(r.PriorValue),
                PreviouslyAbsent = r.PreviouslyAbsent,
            },

            RollbackRecord.RestoreRegistryKey r => new SerializableRollbackRecord
            {
                Type = "restore_registry_key",
                Hive = r.Hive,
                Key = r.Key,
                View = r.View,
                PreviouslyAbsent = r.PreviouslyAbsent,
                ValuesAtKeyLevel = SnapshotsToWire(r.ValuesAtKeyLevel),
            },

            RollbackRecord.RestoreEnv r => new SerializableRollbackRecord
            {
                Type = "restore_env",
                Scope = r.Scope,
                Name = r.Name,
                PriorValueString = r.PriorValue,
                PreviouslyAbsent = r.PreviouslyAbsent,
            },

            RollbackRecord.RestoreDeletedFile r => new SerializableRollbackRecord
            {
                Type = "restore_deleted_file",
                OriginalPath = r.OriginalPath,
                StashPath = r.StashPath,
            },

            RollbackRecord.RestoreDeletedDirectory r => new SerializableRollbackRecord
            {
                Type = "restore_deleted_directory",
                OriginalPath = r.OriginalPath,
                StashPath = r.StashPath,
            },

            // P8: a null StashPath means "the edit created this file" (undo deletes it).
            RollbackRecord.RestoreConfigFile r => new SerializableRollbackRecord
            {
                Type = "restore_config_file",
                OriginalPath = r.OriginalPath,
                StashPath = r.StashPath,
            },

            RollbackRecord.RemoveService r => new SerializableRollbackRecord
            {
                Type = "remove_service",
                ServiceName = r.ServiceName,
            },

            RollbackRecord.DeleteScheduledTask r => new SerializableRollbackRecord
            {
                Type = "delete_scheduled_task",
                TaskName = r.TaskName,
            },

            _ => throw new InvalidOperationException(
                $"unsupported RollbackRecord subtype: {record.GetType().Name}"),
        };
    }

    public static RollbackRecord ToRollbackRecord(this SerializableRollbackRecord s)
    {
        ArgumentNullException.ThrowIfNull(s);

        return s.Type switch
        {
            "restore_file" => new RollbackRecord.RestoreFile(
                s.Path ?? throw MissingField("restore_file", "path"),
                s.ExistedBefore ?? false,
                s.BackupPath),

            "remove_directory" => new RollbackRecord.RemoveDirectory(
                s.Path ?? throw MissingField("remove_directory", "path")),

            "delete_shortcut" => new RollbackRecord.DeleteShortcut(
                s.Path ?? throw MissingField("delete_shortcut", "path")),

            "remove_uninstaller" => new RollbackRecord.RemoveUninstaller(
                s.Path ?? throw MissingField("remove_uninstaller", "path")),

            "restore_registry_value" => new RollbackRecord.RestoreRegistryValue(
                s.Hive ?? throw MissingField("restore_registry_value", "hive"),
                s.Key  ?? throw MissingField("restore_registry_value", "key"),
                s.Name ?? string.Empty,
                s.View ?? "default",
                s.PriorTypeStr,
                JsonElementToObject(s.PriorValue),
                s.PreviouslyAbsent ?? false),

            "restore_registry_key" => new RollbackRecord.RestoreRegistryKey(
                s.Hive ?? throw MissingField("restore_registry_key", "hive"),
                s.Key  ?? throw MissingField("restore_registry_key", "key"),
                s.View ?? "default",
                WireToSnapshots(s.ValuesAtKeyLevel),
                s.PreviouslyAbsent ?? false),

            "restore_env" => new RollbackRecord.RestoreEnv(
                s.Scope ?? throw MissingField("restore_env", "scope"),
                s.Name  ?? throw MissingField("restore_env", "name"),
                s.PriorValueString,
                s.PreviouslyAbsent ?? false),

            "restore_deleted_file" => new RollbackRecord.RestoreDeletedFile(
                s.OriginalPath ?? throw MissingField("restore_deleted_file", "originalPath"),
                s.StashPath ?? throw MissingField("restore_deleted_file", "stashPath")),

            "restore_deleted_directory" => new RollbackRecord.RestoreDeletedDirectory(
                s.OriginalPath ?? throw MissingField("restore_deleted_directory", "originalPath"),
                s.StashPath ?? throw MissingField("restore_deleted_directory", "stashPath")),

            // P8: StashPath is nullable (null = created file → undo deletes it).
            "restore_config_file" => new RollbackRecord.RestoreConfigFile(
                s.OriginalPath ?? throw MissingField("restore_config_file", "originalPath"),
                s.StashPath),

            "remove_service" => new RollbackRecord.RemoveService(
                s.ServiceName ?? throw MissingField("remove_service", "serviceName")),

            "delete_scheduled_task" => new RollbackRecord.DeleteScheduledTask(
                s.TaskName ?? throw MissingField("delete_scheduled_task", "taskName")),

            _ => throw new InvalidOperationException(
                $"unknown rollback record type '{s.Type}'"),
        };
    }

    private static SerializableRegistryValueAtKey[]? SnapshotsToWire(
        IReadOnlyList<RegistryValueSnapshot>? snapshots)
    {
        if (snapshots is null) return null;
        if (snapshots.Count == 0) return Array.Empty<SerializableRegistryValueAtKey>();

        var arr = new SerializableRegistryValueAtKey[snapshots.Count];
        for (var i = 0; i < snapshots.Count; i++)
        {
            arr[i] = new SerializableRegistryValueAtKey
            {
                Name = snapshots[i].Name,
                TypeStr = snapshots[i].TypeStr,
                Value = ObjectToJsonElement(snapshots[i].Value),
            };
        }
        return arr;
    }

    private static RegistryValueSnapshot[] WireToSnapshots(
        SerializableRegistryValueAtKey[]? wire)
    {
        if (wire is null || wire.Length == 0)
        {
            return Array.Empty<RegistryValueSnapshot>();
        }
        var list = new RegistryValueSnapshot[wire.Length];
        for (var i = 0; i < wire.Length; i++)
        {
            list[i] = new RegistryValueSnapshot(
                wire[i].Name,
                wire[i].TypeStr,
                JsonElementToObject(wire[i].Value));
        }
        return list;
    }

    /// <summary>
    /// Convert a captured registry value back to the <c>object?</c> shape
    /// required by <see cref="Microsoft.Win32.RegistryKey.SetValue(string, object)"/>.
    /// Mirrors <see cref="SerializableInstallStepConverter"/>'s logic so the
    /// round-trip preserves the four scalar shapes the install path produces.
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
    /// Wrap an <c>object?</c> registry / shortcut value into a
    /// <see cref="JsonElement"/>. AOT-safe: serializes only via the
    /// source-generated <c>String</c> typing, never <c>JsonSerializer.Serialize&lt;T&gt;</c>
    /// on an <c>object</c> graph.
    /// </summary>
    private static JsonElement? ObjectToJsonElement(object? value)
    {
        if (value is null) return null;

        string json = value switch
        {
            string s       => JsonSerializer.Serialize(s, WrapperBlobJsonContext.Default.String),
            bool b         => b ? "true" : "false",
            int i          => i.ToString(CultureInfo.InvariantCulture),
            long l         => l.ToString(CultureInfo.InvariantCulture),
            JsonElement je => je.GetRawText(),
            _              => JsonSerializer.Serialize(
                                  value.ToString() ?? string.Empty,
                                  WrapperBlobJsonContext.Default.String),
        };

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static InvalidOperationException MissingField(string type, string field) =>
        new($"rollback record of type '{type}' is missing required field '{field}'");
}
