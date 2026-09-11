using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

public sealed record InstallerBrand(
    string? Logo,
    string? Hero,
    string? PrimaryColor,
    string? AccentColor);

/// <summary>
/// Branded Windows installer configuration parsed from the manifest's
/// <c>installer:</c> block.
/// </summary>
/// <remarks>
/// Every member beyond <see cref="Brand"/> is additive and optional: existing
/// construction sites (<c>new InstallerSection(brand)</c>) keep compiling
/// unchanged. This record is the data surface only; parsing and validation live
/// in <c>ManifestParser</c>.
/// </remarks>
/// <param name="Brand">Brand colors + assets.</param>
/// <param name="Options">Built-in configurable components.</param>
/// <param name="Screens">Declared custom wizard screens.</param>
/// <param name="License">License file path (or, per-language, a map of tag to
/// file path); shows the License screen when present. A plain string
/// normalizes to <c>{"en": path}</c> (<see cref="LocalizedText"/>);
/// each declared file is read into text at PACK time
/// (<c>ExeWrapperPackager.ReadLicenseText</c>), never at parse time.</param>
/// <param name="Scope">Install scope; defaults to <see cref="InstallScope.Auto"/>.</param>
/// <param name="InstallDir">Optional install-dir override; may reference
/// <c>{app.*}</c> / <c>{scope_root}</c> tokens.</param>
/// <param name="Icon">Optional custom installer-exe icon path (.ico); when null
/// the packager stamps the bundled default installer icon.</param>
/// <param name="Vars">Named computed values parsed from
/// <c>installer.vars</c>: each is an expression evaluated once at install-session
/// start, in dependency order, and exposed as <c>var.&lt;name&gt;</c> in <c>when</c>
/// expressions / screen-field defaults and as a <c>{var.&lt;name&gt;}</c> brace token
/// in step paths/args. Order is the manifest declaration order (preserved for
/// deterministic packaging); cross-var dependencies are resolved by topological
/// sort — see <see cref="InstallerVarGraph"/>.</param>
/// <param name="Hooks">Lifecycle hooks from <c>installer.hooks</c> —
/// pre/post install + uninstall steps that run OUTSIDE the rollback journal. See
/// <see cref="InstallerHooks"/>.</param>
/// <param name="RunAfterInstall">The <c>installer.run_after_install</c> launch
/// target backing the Done screen's "Launch &lt;App&gt;" checkbox.</param>
/// <param name="Prerequisites">First-class prerequisite units from
/// <c>installer.prerequisites</c> — detect-then-install dependency installers (VC++
/// redist, .NET runtime) that run before the journaled body. See
/// <see cref="InstallerPrerequisite"/>.</param>
/// <param name="AppMutex">Named mutexes the application creates while running —
/// the Inno <c>AppMutex</c> equivalent. Before touching the install
/// dir, setup opens each name; a mutex that opens means the app is running and the
/// install is blocked. Complements the Restart Manager sweep, which finds
/// processes holding files open even when no mutex is declared.</param>
/// <param name="Language">Optional fixed installer language tag from
/// <c>installer.language</c> — the first link in the language-preference
/// chain (installer.language -&gt; /lang -&gt; OS list -&gt; en) resolved by the
/// language resolver in SigilBuild.Wrapper.Core.Localization. <c>null</c> when
/// the manifest doesn't fix a language, letting the OS/flag chain decide.</param>
public sealed record InstallerSection(
    InstallerBrand? Brand,
    InstallerOptions? Options = null,
    IReadOnlyList<InstallerScreen>? Screens = null,
    LocalizedText? License = null,
    InstallScope Scope = InstallScope.Auto,
    string? InstallDir = null,
    string? Icon = null,
    IReadOnlyList<InstallerVar>? Vars = null,
    InstallerHooks? Hooks = null,
    RunAfterInstall? RunAfterInstall = null,
    IReadOnlyList<InstallerPrerequisite>? Prerequisites = null,
    IReadOnlyList<string>? AppMutex = null,
    string? Language = null,
    RequireSignedDownloads RequireSignedDownloads = RequireSignedDownloads.SignDeclared);

/// <summary>
/// The declared policy for whether a binary this run pulled off the network must be
/// Authenticode-valid before it is launched — <c>installer.require_signed_downloads</c>
/// (R45).
/// </summary>
/// <remarks>
/// <c>SignDeclared</c> answers "did this publisher configure signing for their own
/// output", which is a different question from "should downloads be verified": a
/// publisher who signs nothing gets no verification on anything they download and run
/// elevated. This field names the choice instead of inferring it, and
/// <see cref="SignDeclared"/> is the default precisely so that having the field
/// changes no existing manifest's behaviour.
/// </remarks>
public enum RequireSignedDownloads
{
    /// <summary>
    /// Default: the gate is armed if and only if this
    /// manifest declared a <c>sign</c> block. An artifact that never claimed a signed
    /// provenance has no standing to demand one of its successor.
    /// </summary>
    SignDeclared = 0,

    /// <summary>
    /// The gate is always armed, whether or not this manifest declares a <c>sign</c>
    /// block. For a publisher who does not yet code-sign their own installer but only
    /// ever downloads binaries that are signed.
    /// </summary>
    Always = 1,

    /// <summary>
    /// <see cref="Always"/>, and additionally an unestablished revocation status is a
    /// refusal rather than a warning (R46).
    /// </summary>
    /// <remarks>
    /// The default posture lets <c>RevocationUnavailable</c> proceed, because an
    /// installer that cannot reach a CRL distribution point — air-gapped network,
    /// captive portal, locked-down enterprise egress — would otherwise be unable to
    /// install anything. The cost of that default is that anyone who can blackhole two
    /// hostnames suppresses revocation of a stolen signing key. This value moves the
    /// choice to the publisher, who is the only party who knows whether their audience
    /// is reliably online. See <c>docs/architecture/adr-011-update-manifest-freshness.md</c>.
    /// </remarks>
    AlwaysVerifiedRevocation = 2,
}

/// <summary>
/// A single declarative variable from <c>installer.vars</c>: a name bound to
/// an expression (in the closed <c>when</c> grammar) that is evaluated once at
/// install-session start. The result is exposed as <c>var.&lt;Name&gt;</c>.
/// </summary>
/// <param name="Name">The variable name (the <c>var.&lt;Name&gt;</c> identifier and
/// <c>{var.&lt;Name&gt;}</c> brace token; e.g. <c>old_path</c>).</param>
/// <param name="Expression">The expression evaluated to produce the value, e.g.
/// <c>registry_read('HKLM', 'Software\\Acme', 'Path')</c>. May reference other
/// vars as <c>var.&lt;other&gt;</c> (resolved in dependency order).</param>
public sealed record InstallerVar(string Name, string Expression);
