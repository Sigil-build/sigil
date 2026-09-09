namespace SigilBuild.Wrapper.Engine;

using System;
using SigilBuild.Core.Manifest;

/// <summary>
/// The anchoring decision a caller of <see cref="RollbackJournal.UndoAsync"/> must
/// make explicitly (R1). There is no default and no <c>null</c>: a caller replaying
/// persisted state cannot lose anchoring by omitting an argument, only by writing
/// <see cref="InProcess"/> and being wrong about it.
/// </summary>
public sealed class ReplayAnchorage
{
    private ReplayAnchorage(
        string? installDir, string? appId, InstallScope? scope, SignedDeclarations declarations)
    {
        InstallDir = installDir;
        AppId = appId;
        Scope = scope;
        Declarations = declarations;
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
    public static ReplayAnchorage InProcess { get; } =
        new(null, null, null, SignedDeclarations.None);

    /// <summary>
    /// The journal was rehydrated from persisted state and every record must be
    /// checked against <paramref name="installDir"/> before it is replayed. The app
    /// identity is not known, so <strong>no per-app state directory is allowed</strong>
    /// and BOTH scopes' shortcut folders are — the widest of the two anchored forms.
    /// Prefer <see cref="ForInstall"/>, which narrows both.
    /// </summary>
    /// <param name="installDir">The install directory every record is checked against.</param>
    /// <param name="declarations">
    /// What the signed blob declares (R44/R51). Required, not optional, for the same
    /// reason <paramref name="installDir"/> is: an omitted anchoring input is an
    /// invariant the primitive cannot enforce, and one call site forgetting it is all it
    /// takes. Pass <see cref="SignedDeclarations.None"/> when there is genuinely no
    /// signed artefact behind the replay — it only ever narrows.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="installDir"/> is null or blank. Anchoring "to nothing" would be
    /// indistinguishable from <see cref="InProcess"/> at the call site, which is
    /// exactly the mistake this type exists to prevent.
    /// </exception>
    public static ReplayAnchorage ForInstallDir(string installDir, SignedDeclarations declarations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);
        ArgumentNullException.ThrowIfNull(declarations);
        return new ReplayAnchorage(installDir, null, null, declarations);
    }

    /// <summary>
    /// The journal was rehydrated from the persisted state of <paramref name="appId"/>
    /// in <paramref name="scope"/>. Narrower than <see cref="ForInstallDir"/> in two
    /// ways, both of which matter:
    /// <list type="bullet">
    ///   <item>
    ///     only <paramref name="scope"/>'s shortcut folders are allowed, not both scopes';
    ///   </item>
    ///   <item>
    ///     the state-directory allowance is <c>&lt;StateRoot&gt;\Sigil\&lt;AppId&gt;</c> —
    ///     <strong>this</strong> app's — never the shared <c>&lt;StateRoot&gt;\Sigil</c>
    ///     parent. Allowing the parent would let one app's journal delete or overwrite
    ///     another app's <c>uninstall.json</c>; in machine scope the rewritten file comes
    ///     out Administrators-owned and would then pass the provenance gate on the victim
    ///     app's next load, laundering attacker content into trusted state.
    ///   </item>
    /// </list>
    /// </summary>
    /// <param name="installDir">The install directory every record is checked against.</param>
    /// <param name="appId">The app whose per-app state directory is allowed.</param>
    /// <param name="scope">The scope being replayed.</param>
    /// <param name="declarations">
    /// What the signed blob declares (R44/R51): the out-of-tree destinations
    /// <c>allow_outside_install_dir</c> opted out of containment, and the registry keys
    /// the manifest's registry steps name. Required — see <see cref="ForInstallDir"/>.
    /// </param>
    public static ReplayAnchorage ForInstall(
        string installDir, string appId, InstallScope scope, SignedDeclarations declarations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(declarations);
        return new ReplayAnchorage(installDir, appId, scope, declarations);
    }

    /// <summary>The install directory to anchor to, or <c>null</c> for <see cref="InProcess"/>.</summary>
    internal string? InstallDir { get; }

    /// <summary>
    /// The app whose per-app state directory is allowed, or <c>null</c> when the identity
    /// is unknown — in which case no state directory is allowed at all (fail closed).
    /// </summary>
    internal string? AppId { get; }

    /// <summary>
    /// The scope being replayed, or <c>null</c> when unknown — in which case both scopes'
    /// shortcut folders are allowed.
    /// </summary>
    internal InstallScope? Scope { get; }

    /// <summary>
    /// What the SIGNED BLOB declares — never the journal. See
    /// <see cref="SignedDeclarations"/> for why the distinction is the whole point.
    /// </summary>
    internal SignedDeclarations Declarations { get; }

    /// <summary>True when records must be checked before replay.</summary>
    internal bool IsAnchored => InstallDir is not null;
}
