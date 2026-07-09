using System;

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
    public SerializableRollbackRecord[] Records { get; init; }
        = Array.Empty<SerializableRollbackRecord>();
}
