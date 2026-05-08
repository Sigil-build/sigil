using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

/// <summary>
/// Typed-graph representation of a single install-time step parsed from the
/// manifest's <c>install_steps:</c> / <c>pre_install:</c> / <c>post_install:</c>
/// blocks. Each MUST-tier step type from the Sprint 5a catalog is a sealed
/// nested record. Per-step parameter validation lives in
/// <see cref="SigilBuild.Core.Configuration.ManifestParser"/> rather than the
/// home-rolled JSON Schema validator (whose <c>additionalProperties: true</c>
/// on the schema-level step shape intentionally defers to this typed graph).
/// </summary>
public abstract record InstallStep(string Id, string? When, OnFailure OnFailure)
{
    public sealed record FileCopy(
        string Id,
        string From,
        string To,
        bool Overwrite,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record DirectoryCreate(
        string Id,
        string Path,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record FileDelete(
        string Id,
        string Path,
        string IfMissing,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record DirectoryDelete(
        string Id,
        string Path,
        bool Recursive,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record RegistryWrite(
        string Id,
        string Hive,
        string Key,
        string Name,
        string Type,
        object? Value,
        string View,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record RegistryDeleteValue(
        string Id,
        string Hive,
        string Key,
        string Name,
        string View,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record RegistryDeleteKey(
        string Id,
        string Hive,
        string Key,
        bool Recursive,
        string View,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record ShortcutCreate(
        string Id,
        string Target,
        string Location,
        string Name,
        IReadOnlyList<string>? Args,
        string? WorkingDir,
        string? Icon,
        string? Description,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record EnvSet(
        string Id,
        string Name,
        string Value,
        string Scope,
        string Action,
        string Separator,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record RunProgram(
        string Id,
        string Program,
        IReadOnlyList<string>? Args,
        bool Wait,
        string? Cwd,
        IReadOnlyList<int>? ExpectedExitCodes,
        int? TimeoutSeconds,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);
}

/// <summary>
/// What the step engine should do when a step's primary action fails.
/// <list type="bullet">
///   <item><description><c>Rollback</c> — undo the journal up to (and including) this step.</description></item>
///   <item><description><c>Continue</c> — emit a warning and proceed with the next step.</description></item>
///   <item><description><c>Fail</c> — abort the install (default).</description></item>
/// </list>
/// </summary>
public enum OnFailure
{
    Rollback,
    Continue,
    Fail,
}
