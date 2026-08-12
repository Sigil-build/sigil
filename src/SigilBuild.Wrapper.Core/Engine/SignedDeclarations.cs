namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;

/// <summary>
/// What the SIGNED BLOB declares about where this install is allowed to have written
/// (register rows R44 and R51): the destinations a step opted out of install-dir
/// containment with <c>allow_outside_install_dir</c>, and the registry keys the
/// manifest's registry steps name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this type exists at all, and why it is not a journal field.</strong>
/// Both rows need the same thing — "was this coordinate declared?" — and both rejected
/// the same naive answer: a per-record marker in <c>uninstall.json</c> saying "I was
/// declared". The journal is the untrusted artefact. A record carrying that marker is a
/// record saying <em>do not anchor me</em>, so a planted journal could opt itself out of
/// the entire mechanism R1 exists to build. Every value here therefore comes from the
/// running module's <c>SIGIL_BLOB_V1</c> resource — the same bytes Authenticode covers —
/// and <strong>nothing here is ever read from, influenced by, or cross-checked against
/// the journal.</strong>
/// </para>
/// <para>
/// The templates are resolved late, in <see cref="Resolve"/>, against the install
/// directory the replay actually anchors to. That ordering is load-bearing: a declared
/// <c>{install_dir}\…</c> destination must expand to the directory RECORDED at install
/// time (which <c>UninstallEngine.ChooseAnchorDirectory</c> picks), not to a recomputed
/// default, or the declaration lands somewhere the install never wrote.
/// </para>
/// <para>
/// Construction is internal on purpose. There is no public way to fabricate a
/// declaration set, because a fabricated one is exactly what an attacker would want; the
/// only public value is <see cref="None"/>, which can only ever make an anchor
/// <em>stricter</em>.
/// </para>
/// </remarks>
public sealed class SignedDeclarations
{
    private readonly WrapperBlob? _blob;
    private readonly ParsedCommandLine? _parsed;
    private readonly InstallScope _scope;
    private readonly IReadOnlyList<string> _destinationTemplates;
    private readonly IReadOnlyList<DeclaredRegistryKey> _registryTemplates;

    private SignedDeclarations(
        WrapperBlob? blob,
        ParsedCommandLine? parsed,
        InstallScope scope,
        IReadOnlyList<string> destinationTemplates,
        IReadOnlyList<DeclaredRegistryKey> registryTemplates)
    {
        _blob = blob;
        _parsed = parsed;
        _scope = scope;
        _destinationTemplates = destinationTemplates;
        _registryTemplates = registryTemplates;
    }

    /// <summary>
    /// The empty declaration set: no out-of-tree destination is anchored and NO registry
    /// record may replay at all.
    /// </summary>
    /// <remarks>
    /// Safe to pass anywhere, in the one direction that matters: it can only narrow an
    /// anchor, never widen one. It is the right value for an anchorage that has no signed
    /// blob behind it — a test, or a caller replaying a journal it did not author and
    /// cannot corroborate. It is NOT a way to "skip" declarations at a production
    /// uninstall: that call site takes the blob's set, and passing this instead would
    /// refuse every registry record of a healthy uninstall, loudly.
    /// </remarks>
    public static SignedDeclarations None { get; } =
        new(null, null, InstallScope.User, Array.Empty<string>(), Array.Empty<DeclaredRegistryKey>());

    /// <summary>
    /// Collect the declarations out of <paramref name="blob"/> — the steps as the signed
    /// artefact states them, before any token expansion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every journaled step list is scanned. Hook steps are deliberately NOT scanned:
    /// hooks run outside the rollback journal (<c>HookRunner</c>), so no hook step can
    /// produce a record for the anchor to judge, and including them would widen the
    /// anchor for coordinates no record can name.
    /// </para>
    /// <para>
    /// Only the destination field the step's own containment guard checks is collected
    /// (<c>file_copy.to</c>, never <c>file_copy.from</c>) and only when the step carries
    /// <c>allow_outside_install_dir</c> — a step without the opt-out is contained to
    /// <c>install_dir</c>, which the anchor already covers.
    /// </para>
    /// </remarks>
    internal static SignedDeclarations FromBlob(
        WrapperBlob blob, ParsedCommandLine parsed, InstallScope scope)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(parsed);

        var destinations = new List<string>();
        var registryKeys = new List<DeclaredRegistryKey>();

        CollectFrom(blob.InstallSteps, destinations, registryKeys);
        CollectFrom(blob.PreInstall, destinations, registryKeys);
        CollectFrom(blob.PostInstall, destinations, registryKeys);
        CollectFrom(blob.UpdateSteps, destinations, registryKeys);

        return new SignedDeclarations(blob, parsed, scope, destinations, registryKeys);
    }

    /// <summary>
    /// A declaration set built from already-literal coordinates, for tests and for
    /// callers that have the declared values but no blob to re-read. The values are
    /// still passed through <see cref="Resolve"/>'s floor — a literal is not privileged.
    /// </summary>
    internal static SignedDeclarations ForLiterals(
        IEnumerable<string>? outOfTreeDestinations,
        IEnumerable<DeclaredRegistryKey>? registryKeys)
    {
        var destinations = outOfTreeDestinations is null
            ? new List<string>()
            : new List<string>(outOfTreeDestinations);
        var keys = registryKeys is null
            ? new List<DeclaredRegistryKey>()
            : new List<DeclaredRegistryKey>(registryKeys);
        return new SignedDeclarations(null, null, InstallScope.User, destinations, keys);
    }

    /// <summary>
    /// Expand the declared templates against <paramref name="installDir"/> — the
    /// directory the replay is anchored to, which is the one recorded at install time.
    /// </summary>
    /// <remarks>
    /// A template that cannot be resolved (an unresolved <c>{token}</c>, an unknown
    /// <c>${identifier}</c>, a <c>payload://</c> destination with no payload) contributes
    /// NOTHING and is reported. Uninstall knows the parameter defaults but not the values
    /// a wizard collected at install time, so an unresolvable declaration is a
    /// declaration this process cannot corroborate — and an anchor widened on a guess is
    /// not an anchor.
    /// </remarks>
    internal ResolvedDeclarations Resolve(string installDir)
    {
        if (_destinationTemplates.Count == 0 && _registryTemplates.Count == 0)
        {
            return ResolvedDeclarations.Empty;
        }

        var notices = new List<string>();

        // A literal-only set (tests, and any caller holding the declared values without a
        // blob to re-read) needs no expansion: its templates ARE the coordinates.
        if (_blob is null || _parsed is null)
        {
            return new ResolvedDeclarations(
                new List<string>(_destinationTemplates),
                new List<DeclaredRegistryKey>(_registryTemplates),
                notices);
        }

        var ctx = BuildContext(installDir, notices);
        if (ctx is null)
        {
            return new ResolvedDeclarations(
                Array.Empty<string>(), Array.Empty<DeclaredRegistryKey>(), notices);
        }

        var destinations = new List<string>();
        foreach (var template in _destinationTemplates)
        {
            var resolved = TryResolvePath(ctx, template, notices);
            if (resolved is not null)
            {
                destinations.Add(resolved);
            }
        }

        var keys = new List<DeclaredRegistryKey>();
        foreach (var declared in _registryTemplates)
        {
            var resolved = TryResolveTemplate(ctx, declared.Key, notices);
            if (resolved is not null)
            {
                keys.Add(declared with { Key = resolved });
            }
        }

        return new ResolvedDeclarations(destinations, keys, notices);
    }

    /// <summary>
    /// Build the resolution context for the blob-backed set, or <c>null</c> when it
    /// cannot be built — in which case nothing is declared and the anchor stays as narrow
    /// as it was before this lane.
    /// </summary>
    private StepContext? BuildContext(string installDir, List<string> notices)
    {
#pragma warning disable CA1031 // Fail closed: a context that cannot be built declares nothing.
        try
        {
            return StepContext.From(
                _blob!, _parsed!, scope: _scope, collectedInstallDir: installDir);
        }
        catch (Exception ex)
        {
            notices.Add(
                "declared out-of-tree destinations and registry keys could not be resolved " +
                $"({ex.Message}); the replay is anchored as if the manifest declared none");
            return null;
        }
#pragma warning restore CA1031
    }

    private static string? TryResolvePath(StepContext ctx, string template, List<string> notices)
    {
#pragma warning disable CA1031 // Fail closed: an unresolvable declaration widens nothing.
        try
        {
            var resolved = ctx.ResolvePath(template);
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }
        catch (Exception ex)
        {
            notices.Add(
                $"the declared out-of-tree destination '{template}' could not be resolved at " +
                $"uninstall time ({ex.Message}), so records naming it are not anchored by it");
            return null;
        }
#pragma warning restore CA1031
    }

    private static string? TryResolveTemplate(StepContext ctx, string template, List<string> notices)
    {
#pragma warning disable CA1031 // Fail closed: an unresolvable declaration widens nothing.
        try
        {
            var resolved = ctx.Resolve(template);
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }
        catch (Exception ex)
        {
            notices.Add(
                $"the declared registry key '{template}' could not be resolved at uninstall " +
                $"time ({ex.Message}), so records naming it are not anchored by it");
            return null;
        }
#pragma warning restore CA1031
    }

    private static void CollectFrom(
        IReadOnlyList<InstallStep>? steps,
        List<string> destinations,
        List<DeclaredRegistryKey> registryKeys)
    {
        if (steps is null)
        {
            return;
        }

        foreach (var step in steps)
        {
            switch (step)
            {
                // --- R51: every step type that journals a registry record ---
                //
                // The three below are the COMPLETE set of producers of
                // RestoreRegistryValue / RestoreRegistryKey; RegistryRecordProducerTests
                // fails if a fourth appears, because a new producer whose keys are not
                // collected here makes its own install unremovable.
                case InstallStep.RegistryWrite r:
                    registryKeys.Add(new DeclaredRegistryKey(r.Hive, r.Key));
                    break;
                case InstallStep.RegistryDeleteValue r:
                    registryKeys.Add(new DeclaredRegistryKey(r.Hive, r.Key));
                    break;
                case InstallStep.RegistryDeleteKey r:
                    registryKeys.Add(new DeclaredRegistryKey(r.Hive, r.Key));
                    break;

                // --- R44: the destination fields StepDestinationGuard contains ---
                case InstallStep.FileCopy s when s.AllowOutsideInstallDir:
                    destinations.Add(s.To);
                    break;
                case InstallStep.DirectoryCreate s when s.AllowOutsideInstallDir:
                    destinations.Add(s.Path);
                    break;
                case InstallStep.FileDelete s when s.AllowOutsideInstallDir:
                    destinations.Add(s.Path);
                    break;
                case InstallStep.DirectoryDelete s when s.AllowOutsideInstallDir:
                    destinations.Add(s.Path);
                    break;
                case InstallStep.HttpDownload s when s.AllowOutsideInstallDir:
                    destinations.Add(s.Dest);
                    break;
                case InstallStep.IniWrite s when s.AllowOutsideInstallDir:
                    destinations.Add(s.Path);
                    break;
                case InstallStep.JsonEdit s when s.AllowOutsideInstallDir:
                    destinations.Add(s.Path);
                    break;
                case InstallStep.XmlEdit s when s.AllowOutsideInstallDir:
                    destinations.Add(s.Path);
                    break;
                default:
                    break;
            }
        }
    }
}

/// <summary>
/// One registry coordinate the manifest declares, as the manifest spells it — the
/// <c>hive:</c> field verbatim and the <c>key:</c> template.
/// </summary>
internal readonly record struct DeclaredRegistryKey(string Hive, string Key);

/// <summary>
/// <see cref="SignedDeclarations"/> after token expansion: literal destinations and
/// literal registry coordinates, plus the operator-facing lines explaining anything that
/// was dropped on the way.
/// </summary>
/// <remarks>
/// These have been RESOLVED, not yet VETTED. <c>ReplayAnchor</c> applies the floor (a
/// declaration may not be a volume root, a well-known folder, inside the Windows
/// directory, or reached through a junction) and folds the registry coordinates, so that
/// every anchor gets the same treatment no matter which caller produced the set.
/// </remarks>
internal sealed record ResolvedDeclarations(
    IReadOnlyList<string> Destinations,
    IReadOnlyList<DeclaredRegistryKey> RegistryKeys,
    IReadOnlyList<string> Notices)
{
    /// <summary>Nothing declared, nothing to report.</summary>
    public static ResolvedDeclarations Empty { get; } = new(
        Array.Empty<string>(), Array.Empty<DeclaredRegistryKey>(), Array.Empty<string>());
}
