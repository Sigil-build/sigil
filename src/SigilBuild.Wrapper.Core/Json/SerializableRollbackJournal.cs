using System;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Wrapper.Json;

/// <summary>
/// Top-level wire DTO for the persisted rollback journal. Lives at
/// <c>%ProgramData%\Sigil\&lt;AppId&gt;\uninstall.json</c> after a successful
/// install; consumed by <c>UninstallEngine</c> to reverse the installation.
/// </summary>
/// <remarks>
/// <see cref="Version"/> is a forward-compatibility hatch — Task 19 emits
/// <c>"1"</c>. Bumping the schema in a later sprint must be paired with a
/// reader fallback that returns a clear error for unknown versions.
/// </remarks>
internal sealed record SerializableRollbackJournal
{
    public string AppId { get; init; } = string.Empty;
    public string Version { get; init; } = "1";

    /// <summary>
    /// The install scope this state was written under (T12). <strong>Written, never
    /// read.</strong> R1 clause (b): <c>UninstallStateStore.Load</c> used to take the
    /// authoritative scope from this field, so a file planted in the user-scope
    /// directory could claim <c>machine</c> and steer an uninstall onto the HKLM ARP
    /// hive and the <c>%ProgramData%</c> state directory. The scope now comes from the
    /// directory the file was found in; this field is retained only so state written
    /// before the fix still deserializes, and must never be consumed again — a value
    /// inside a file whose trustworthiness is in question cannot decide the privilege
    /// that file is handled with. Defaults to <see cref="InstallScope.User"/> for
    /// state files written before T12.
    /// </summary>
    public InstallScope Scope { get; init; } = InstallScope.User;

    /// <summary>
    /// The directory this install actually landed in — <c>StepContext.InstallDir</c>,
    /// i.e. the resolved <c>/D=</c> / manifest / wizard / default destination, and the
    /// same value written to the ARP <c>InstallLocation</c>. Recorded so the uninstall
    /// can anchor the replay to where the files really are (R1 clause (c)) rather than
    /// recomputing a default: an install into a wizard-chosen or <c>/D=</c> directory
    /// would otherwise have every one of its file records refused and become silently
    /// unremovable. <c>null</c> for state written before this field existed; the reader
    /// then falls back to the directory it resolved for the current run.
    /// </summary>
    public string? InstallDir { get; init; }

    public SerializableRollbackRecord[] Records { get; init; }
        = Array.Empty<SerializableRollbackRecord>();
}
