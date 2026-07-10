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
    /// The install scope this state was written under (T12). Recorded so an
    /// uninstall runs in the same scope it was installed with — the ARP hive and
    /// the state directory both follow it. Defaults to
    /// <see cref="InstallScope.User"/> for state files written before T12.
    /// </summary>
    public InstallScope Scope { get; init; } = InstallScope.User;

    public SerializableRollbackRecord[] Records { get; init; }
        = Array.Empty<SerializableRollbackRecord>();
}
