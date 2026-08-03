namespace SigilBuild.Wrapper.Engine;

using System;

/// <summary>
/// The anchoring decision a caller of <see cref="RollbackJournal.UndoAsync"/> must
/// make explicitly (R1). There is no default and no <c>null</c>: a caller replaying
/// persisted state cannot lose anchoring by omitting an argument, only by writing
/// <see cref="InProcess"/> and being wrong about it.
/// </summary>
public sealed class ReplayAnchorage
{
    private ReplayAnchorage(string? installDir)
    {
        InstallDir = installDir;
    }

    /// <summary>
    /// The journal was built in memory by this process during this run — the
    /// mid-install rollback path. Every record was authored moments ago by the engine
    /// itself from the signed manifest; nothing has round-tripped through a file an
    /// attacker can write, so there is nothing to anchor against, and anchoring it
    /// would refuse legitimate reversals of manifest-declared work outside the install
    /// directory.
    /// <para>
    /// <strong>Never use this for a journal that came off disk.</strong> That is
    /// register row R1.
    /// </para>
    /// </summary>
    public static ReplayAnchorage InProcess { get; } = new(null);

    /// <summary>
    /// The journal was rehydrated from persisted state and every record must be
    /// checked against <paramref name="installDir"/> before it is replayed.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="installDir"/> is null or blank. Anchoring "to nothing" would be
    /// indistinguishable from <see cref="InProcess"/> at the call site, which is
    /// exactly the mistake this type exists to prevent.
    /// </exception>
    public static ReplayAnchorage ForInstallDir(string installDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);
        return new ReplayAnchorage(installDir);
    }

    /// <summary>The install directory to anchor to, or <c>null</c> for <see cref="InProcess"/>.</summary>
    internal string? InstallDir { get; }

    /// <summary>True when records must be checked before replay.</summary>
    internal bool IsAnchored => InstallDir is not null;
}
