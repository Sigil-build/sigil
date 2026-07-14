using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

/// <summary>
/// A first-class prerequisite unit (P5, gap G6) parsed from
/// <c>installer.prerequisites[]</c> — the declarative equivalent of Burn's
/// <c>ExePackage</c> + <c>DetectCondition</c>. Prerequisites run sequentially
/// BEFORE the transactional install body and the P2 <c>pre_install</c> hooks (and
/// before the rollback journal opens): each one's <see cref="Detect"/> expression is
/// evaluated (skip when already satisfied), otherwise the <see cref="Source"/>
/// installer is acquired and run, and <see cref="Detect"/> is re-evaluated to confirm
/// it took effect.
/// </summary>
/// <remarks>
/// Prerequisites are NEVER journaled and are NOT rolled back — a VC++ redist or a
/// .NET runtime is a shared, machine-level dependency that other apps rely on, so
/// undoing it would be wrong. This is loud in the schema and docs.
/// </remarks>
/// <param name="Name">Human label shown on the wizard progress row and in the log
/// ("Installing &lt;name&gt;…").</param>
/// <param name="Detect">A <c>when</c>-grammar expression that is <c>true</c> when the
/// prerequisite is already satisfied (typically a <c>registry_read</c> /
/// <c>file_version</c> / <c>registry_exists</c> check).</param>
/// <param name="Source"><c>payload://…</c> (bundled in the package) or <c>https://…</c>
/// (downloaded at install time) installer to run when detect is false.</param>
/// <param name="Sha256">REQUIRED for an <c>https://</c> source (a pack-time diagnostic
/// otherwise); ignored for a <c>payload://</c> source (integrity comes from the signed
/// package). Hex, case-insensitive.</param>
/// <param name="Args">Arguments passed to the source installer (typically
/// <c>/quiet /norestart</c>). Each may use <c>{var.*}</c> / <c>{install_dir}</c> tokens.</param>
/// <param name="ExitCodesOk">Exit codes treated as success; defaults to <c>[0]</c>. An
/// accepted code of <c>3010</c> additionally flags reboot-required for the session.</param>
/// <param name="ScopeRequired"><c>allusers</c> (needs a per-machine / elevated install)
/// or <c>currentuser</c> — a mismatch with the resolved install scope is a diagnostic at
/// session start; <c>null</c> means any scope is acceptable.</param>
/// <param name="TimeoutSeconds">Optional per-prerequisite run timeout, in seconds.</param>
public sealed record InstallerPrerequisite(
    string Name,
    string Detect,
    string Source,
    string? Sha256 = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyList<int>? ExitCodesOk = null,
    string? ScopeRequired = null,
    int? TimeoutSeconds = null);
