namespace SigilBuild.Wrapper.Engine;

using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Core.Localization;

/// <summary>
/// Immutable view over the resolved environment for a single install run.
/// Backs both expression evaluation (<c>When</c> clauses) and string
/// substitution inside step parameters via <see cref="Resolve"/>.
/// </summary>
public sealed class StepContext
{
    private const string PayloadScheme = "payload://";

    private readonly System.Collections.Generic.IReadOnlyDictionary<string, object?> _values;
    private readonly System.Collections.Generic.IReadOnlyList<string> _secretValues;
    private readonly Expressions.Evaluator _evaluator = new();
    private readonly string? _appName;
    private readonly string _appId;

    public StepContext(
        System.Collections.Generic.IReadOnlyDictionary<string, object?> values,
        string? payloadRoot = null,
        System.Collections.Generic.IReadOnlyList<string>? secretValues = null,
        InstallScope scope = InstallScope.User,
        string? installDir = null,
        string? appName = null,
        string? appId = null)
    {
        System.ArgumentNullException.ThrowIfNull(values);
        _values = values;
        _secretValues = secretValues ?? System.Array.Empty<string>();
        PayloadRoot = payloadRoot;
        Layout = ScopeLayout.For(scope);
        InstallDir = installDir;
        _appName = appName;
        _appId = appId ?? "<unset>";
    }

    /// <summary>
    /// The resolved effective install directory for this run (T13): the
    /// destination that the <c>{install_dir}</c> token expands to in step paths and
    /// expressions. Computed by <see cref="InstallDirResolver"/> from the scope,
    /// the manifest override, and the <c>/D=</c> / wizard overrides. <c>null</c> for
    /// a context built without one (e.g. <see cref="Empty"/> and the step unit
    /// tests), in which case a literal <c>{install_dir}</c> is left unsubstituted.
    /// </summary>
    public string? InstallDir { get; }

    public static StepContext Empty { get; } =
        new StepContext(new System.Collections.Generic.Dictionary<string, object?>());

    /// <summary>
    /// Optional sink a long-running step (P4 <c>http_download</c>) uses to emit
    /// intra-step progress rows (download percentage / retry notices). Set by
    /// <see cref="InstallEngine"/> to the run's progress channel, so the rows reach
    /// the wizard progress screen and the /LOG file. Message-only rows report
    /// <c>Total = 0</c> so they never move the overall progress bar.
    /// </summary>
    internal System.IProgress<StepProgress>? ProgressSink { get; set; }

    /// <summary>
    /// The resolved per-scope layout for this run (T12): install root, ARP hive,
    /// PATH scope, and shortcut folders. Scope-varying steps
    /// (<see cref="Steps.EnvSetStep"/>, <see cref="Steps.ShortcutCreateStep"/>)
    /// consult this rather than hardcoding machine/user paths. Defaults to
    /// per-user for a context built without an explicit scope (e.g.
    /// <see cref="Empty"/> and the step unit tests).
    /// </summary>
    public ScopeLayout Layout { get; }

    /// <summary>The resolved install scope (<see cref="InstallScope.User"/> / <see cref="InstallScope.Machine"/>).</summary>
    public InstallScope Scope => Layout.Scope;

    /// <summary>
    /// The resolved string values of every <see cref="ParameterType.Secret"/>
    /// parameter for this run (deduplicated, empty values excluded). Consumed by
    /// the completion path (<c>UninstallStateStore</c>) and the engine's log
    /// redaction so secrets never reach persisted state or log output
    /// (decision 6).
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<string> SecretValues => _secretValues;

    /// <summary>
    /// Replace every occurrence of a secret parameter value in
    /// <paramref name="text"/> with <c>***</c>. Defense-in-depth for any log or
    /// journal line that might interpolate a resolved secret; a no-op when the
    /// run declares no secrets.
    /// </summary>
    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text) || _secretValues.Count == 0)
        {
            return text;
        }

        var result = text;
        foreach (var secret in _secretValues)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                result = result.Replace(secret, "***", System.StringComparison.Ordinal);
            }
        }
        return result;
    }

    /// <summary>
    /// Absolute path to the temp directory into which the embedded
    /// <c>SIGIL_PAYLOAD_V1</c> archive was extracted for this run, or
    /// <c>null</c> when the running exe carries no payload (an un-stamped dev
    /// runtime). Steps resolve <c>payload://relative/path</c> sources against
    /// it via <see cref="ResolvePath"/>. The directory's lifetime is owned by
    /// <see cref="InstallSession"/>, which deletes it once the run completes
    /// (on success, failure, cancel, or rollback).
    /// </summary>
    public string? PayloadRoot { get; }

    /// <summary>
    /// Build a <see cref="StepContext"/> by materializing parameter overrides
    /// from <paramref name="parsed"/> against the schema in <paramref name="blob"/>,
    /// then layering the <c>system.*</c> and <c>env.*</c> namespaces used by
    /// the expression evaluator's <c>When</c> clauses and by
    /// <see cref="Resolve"/> templates.
    /// </summary>
    /// <remarks>
    /// Resolution precedence for each declared parameter is
    /// CLI override → schema default → <c>null</c>. Undeclared CLI params
    /// can never reach this method — <see cref="CommandLineParser.Parse"/>
    /// rejects them up-front.
    /// </remarks>
    internal static StepContext From(
        WrapperBlob blob,
        ParsedCommandLine parsed,
        string? payloadRoot = null,
        System.Collections.Generic.IReadOnlyDictionary<string, string>? collected = null,
        InstallScope scope = InstallScope.User,
        System.Collections.Generic.IReadOnlyDictionary<string, bool>? collectedOptions = null,
        string? collectedInstallDir = null,
        string? priorInstallDir = null)
    {
        System.ArgumentNullException.ThrowIfNull(blob);
        System.ArgumentNullException.ThrowIfNull(parsed);

        var layout = ScopeLayout.For(scope);

        // T13: resolve the effective install dir once, up front, so the
        // {install_dir} token expands to a concrete directory in every step path
        // and expression. Precedence: wizard-collected → /D= → prior install dir
        // (P3 upgrade) → manifest override → default (<scope root>\<App.Name>).
        var installDir = InstallDirResolver.Resolve(
            scope: layout.Scope,
            appName: blob.AppName,
            appId: blob.AppId,
            manifestInstallDir: blob.InstallDir,
            cliOverride: parsed.InstallDir,
            collected: collectedInstallDir,
            priorInstallDir: priorInstallDir);

        var dict = new System.Collections.Generic.Dictionary<string, object?>(System.StringComparer.Ordinal);
        var secrets = new System.Collections.Generic.List<string>();
        // Secret identifier keys (param.<name> / parameters.<name> of a Secret
        // parameter) so P1 vars can inherit secretness (ADR-008 §3). VarResolver
        // extends this with any tainted var.<name>.
        var secretIds = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

        // Materialise parameter values. Precedence: GUI-collected (wizard) →
        // CLI /P override → schema default → null. Both the canonical
        // `parameters.<name>` and the shorthand `param.<name>` namespaces are
        // exposed so manifests can write either in a step `when` (the reference
        // manifest uses `param.autostart`).
        foreach (var def in blob.Parameters)
        {
            object? value;
            if (collected is not null && collected.TryGetValue(def.Name, out var g))
            {
                value = ConvertToTyped(g, def.Type);
            }
            else if (parsed.Values.TryGetValue(def.Name, out var v))
            {
                value = ConvertToTyped(v, def.Type);
            }
            else
            {
                value = def.Default;
            }

            dict["parameters." + def.Name] = value;
            dict["param." + def.Name] = value;

            if (def.Type == ParameterType.Secret)
            {
                // Mark the identifier secret regardless of the current value so a
                // var referencing an unset secret param is still tainted.
                secretIds.Add("param." + def.Name);
                secretIds.Add("parameters." + def.Name);
                if (value is string sv && sv.Length > 0)
                {
                    secrets.Add(sv);
                }
            }
        }

        // App metadata (ported from PR #8) — exposes the manifest's `app:` block
        // as `${app.*}` in step values (e.g. a registry_write writing
        // `${app.version}` / `${app.publisher}`). Sourced from the blob's T10 ARP
        // fields (DisplayName/Publisher/Version) plus AppId/AppName. Without these
        // the placeholders would land in the registry as literal text.
        dict["app.id"] = blob.AppId;
        dict["app.name"] = blob.AppName ?? blob.DisplayName ?? blob.AppId;
        dict["app.version"] = blob.Version ?? string.Empty;
        dict["app.publisher"] = blob.Publisher ?? string.Empty;

        // System context (used by the expression evaluator's `system.*` namespace).
        dict["system.os"] = System.Environment.OSVersion.Version.ToString();
        dict["system.arch"] = System.Runtime.InteropServices.RuntimeInformation
                                  .ProcessArchitecture.ToString().ToLowerInvariant();
        // P9 (gap G10): the resolved CHROME language's tag, not the top OS
        // preference — design §4.3: with OS prefs [de-DE, uk-UA] and en+uk
        // chrome, system.language reads "uk" because that's what the UI
        // actually renders, not "de". Mirrors the established
        // `_lang.ToString().ToLowerInvariant()` pattern (InstallerViewModel);
        // never CultureInfo. Real entry points resolve SessionLanguage once at
        // session start, before any StepContext is built here. Guarded on
        // IsSet (rather than reading .Current directly) so the hundreds of
        // engine tests that build a StepContext without ever calling
        // ResolveSessionLanguage keep working under a Debug test run — this is
        // a convenience expression value, not UI construction, so it degrades
        // quietly to the same "en" the Release-mode fallback would give,
        // without tripping SessionLanguage.Current's DEBUG throw guard.
        dict["system.language"] = (SessionLanguage.IsSet ? SessionLanguage.Current : Lang.En)
            .ToString().ToLowerInvariant();

        // Env context (only the well-known PATH for now; full env exposure is policy-deferred).
        dict["env.PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        // Scope context (T12): the resolved install scope as a bare `scope`
        // identifier (usable in a step `when: "scope == \"machine\""`) plus the
        // per-scope install root as `scope.root` (the default install-dir base,
        // T13). Both `scope` and `scope.root` mirror how T9 exposed `param.*`.
        dict["scope"] = layout.Name;
        dict["scope.root"] = layout.InstallRoot;

        // T13: expose the resolved install dir + scope root as dotted identifiers
        // too, mirroring the `{install_dir}` / `{scope_root}` brace tokens the step
        // paths + `when` expressions use (SubstituteBraceTokens handles the brace
        // form). A `when: "install_dir == '...'"` reads the dotted form here.
        dict["install_dir"] = installDir;
        dict["scope_root"] = layout.InstallRoot;

        // Option context (T8): expose each ENABLED built-in component as
        // `option.<name>` so the auto-generated, option-gated steps evaluate AND a
        // hand-written step can gate on `option.*`. Resolution precedence mirrors
        // parameters: a `locked` component is fixed at its default (the user can't
        // change it); otherwise GUI-collected checkbox → CLI `/P<name>` override →
        // component default. Mirrors how T9 seeded `param.*` and T12 seeded `scope`.
        if (blob.Options is { } options)
        {
            foreach (var opt in options)
            {
                // P10 (gap G11): a custom component with a `when` gate that evaluates
                // false is not applicable to this run — its option resolves off
                // (hiding the row in the wizard and skipping any step it gates),
                // regardless of default / checkbox / CLI. A malformed/erroring `when`
                // fails open (applicable), matching the wizard's fail-open row policy.
                if (opt.Custom && !string.IsNullOrWhiteSpace(opt.When) && !EvaluateGate(opt.When!, dict))
                {
                    dict["option." + opt.Name] = false;
                    continue;
                }

                // A custom component's CLI override is namespaced under `option.<name>`
                // (so it never collides with a same-named parameter); a built-in's is
                // the bare `<name>`. Both live in parsed.Options keyed exactly as typed.
                var cliKey = opt.Custom ? "option." + opt.Name : opt.Name;

                bool value;
                if (opt.Locked)
                {
                    value = opt.Default;
                }
                else if (collectedOptions is not null && collectedOptions.TryGetValue(opt.Name, out var g))
                {
                    value = g;
                }
                else if (parsed.Options.TryGetValue(cliKey, out var cli) && bool.TryParse(cli, out var cliB))
                {
                    value = cliB;
                }
                else
                {
                    value = opt.Default;
                }

                dict["option." + opt.Name] = value;
            }
        }

        // P1: evaluate installer.vars once, now that every base identifier is
        // seeded. Each result is exposed as var.<name> (usable in `when`, screen
        // defaults, and {var.<name>} brace tokens). Secret-derived vars inherit
        // secretness and land in `secrets` for redaction.
        VarResolver.Populate(blob.Vars, dict, new Expressions.Evaluator(), secretIds, secrets);

        return new StepContext(dict, payloadRoot, secrets, layout.Scope, installDir, blob.AppName, blob.AppId);
    }

    /// <summary>
    /// Evaluate a custom component's applicability <c>when</c> (P10) against the
    /// base identifiers seeded so far. Runs before <c>installer.vars</c> are
    /// populated, so a component's <c>when</c> may reference <c>param.*</c> /
    /// <c>scope</c> / <c>system.*</c> / prior <c>option.*</c> but not <c>var.*</c>
    /// (a v1 limitation). Fails open (applicable) on a malformed / erroring
    /// expression, matching the wizard's fail-open row-visibility policy.
    /// </summary>
    private static bool EvaluateGate(
        string expression, System.Collections.Generic.IReadOnlyDictionary<string, object?> values)
    {
        try
        {
            return new Expressions.Evaluator().EvaluateBool(expression, values);
        }
#pragma warning disable CA1031 // Fail-open: a bad `when` must not hide the component or crash the run.
        catch (System.Exception)
        {
            return true;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Convert a raw string value (from the wizard or a <c>/P</c> override) to the
    /// CLR type the expression engine expects for the declared parameter type:
    /// <c>bool</c> for <see cref="ParameterType.Bool"/>, <c>int</c> for
    /// <see cref="ParameterType.Int"/>, otherwise the string unchanged. A value
    /// that fails to parse falls through as the raw string so a later validation
    /// pass surfaces it rather than throwing here.
    /// </summary>
    private static object? ConvertToTyped(string? raw, ParameterType type)
    {
        if (raw is null)
        {
            return null;
        }
        return type switch
        {
            ParameterType.Bool => bool.TryParse(raw, out var b) ? b : (object)raw,
            ParameterType.Int => int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var i)
                ? i
                : (object)raw,
            _ => raw,
        };
    }

    /// <summary>Substitute <c>${parameters.foo}</c> patterns in <paramref name="template"/>.</summary>
    public string Resolve(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        // Simple ${path} substitution; no recursion, no defaults.
        var sb = new System.Text.StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '$' && i + 1 < template.Length && template[i + 1] == '{')
            {
                var end = template.IndexOf('}', i + 2);
                if (end < 0)
                {
                    throw new System.FormatException("unterminated ${...} in template");
                }

                var path = template.Substring(i + 2, end - i - 2);
                if (!_values.TryGetValue(path, out var v))
                {
                    throw new System.FormatException(
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"unknown identifier '{path}' in template"));
                }

                sb.Append(v?.ToString() ?? string.Empty);
                i = end + 1;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }
        return SubstituteBraceTokens(sb.ToString());
    }

    /// <summary>
    /// Substitute the single-brace runtime tokens — <c>{install_dir}</c>,
    /// <c>{scope_root}</c>, <c>{app.name}</c>, <c>{app.id}</c>, <c>{temp_dir}</c> —
    /// that step paths and <c>when</c> expressions use (distinct from the
    /// <c>${...}</c> parameter templates handled by <see cref="Resolve"/>). This is
    /// what turns a step <c>to: "{install_dir}/app.txt"</c> into a real directory
    /// rather than a literal <c>{install_dir}</c> folder (T13). An unknown brace
    /// token is left untouched; <c>{install_dir}</c> is left literal only when
    /// this context was built without a resolved install dir (e.g. the step unit
    /// tests).
    /// </summary>
    private string SubstituteBraceTokens(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('{', System.StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var result = text;
        if (InstallDir is not null)
        {
            result = result.Replace("{install_dir}", InstallDir, System.StringComparison.Ordinal);
        }
        result = result.Replace("{scope_root}", Layout.InstallRoot, System.StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(_appName))
        {
            result = result.Replace("{app.name}", _appName, System.StringComparison.Ordinal);
        }
        result = result.Replace("{app.id}", _appId, System.StringComparison.Ordinal);
        // P12 (T12.5): the web-installer stub's synthesized http_download `dest`
        // needs a temp location resolvable at INSTALL time (the stub's blob is
        // packed with the literal token, so packing stays deterministic — no
        // GUID/timestamp is ever baked in). Resolves to the per-user temp
        // directory (trimmed of its trailing separator so callers can safely
        // append "/relative/path").
        if (result.Contains("{temp_dir}", System.StringComparison.Ordinal))
        {
            var tempDir = System.IO.Path.GetTempPath()
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            result = result.Replace("{temp_dir}", tempDir, System.StringComparison.Ordinal);
        }
        result = ReplaceVarTokens(result);
        return result;
    }

    /// <summary>
    /// Expand <c>{var.&lt;name&gt;}</c> brace tokens (P1) against the evaluated
    /// <c>installer.vars</c> seeded in the context. A token whose var was not
    /// declared is left literal (mirroring the unknown-brace-token behaviour of the
    /// fixed tokens). This is the cross-step data-flow channel: a step
    /// <c>to: "{var.old_path}/app.txt"</c> lands under a directory read from the
    /// registry at session start.
    /// </summary>
    private string ReplaceVarTokens(string text)
    {
        const string prefix = "{var.";
        if (text.IndexOf(prefix, System.StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '{'
                && string.CompareOrdinal(text, i, prefix, 0, prefix.Length) == 0)
            {
                var end = text.IndexOf('}', i + 1);
                if (end > i)
                {
                    // token is the identifier without braces, e.g. "var.old_path"
                    var token = text.Substring(i + 1, end - i - 1);
                    if (_values.TryGetValue(token, out var v))
                    {
                        sb.Append(v?.ToString() ?? string.Empty);
                        i = end + 1;
                        continue;
                    }
                }
                // Unknown var or unterminated brace — leave the '{' literal.
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolve a path-valued step field: first expand <c>${...}</c> templates
    /// via <see cref="Resolve"/>, then — if the result begins with the
    /// <c>payload://</c> scheme — rebase the remainder onto
    /// <see cref="PayloadRoot"/> (the extracted embedded payload). Non-payload
    /// paths pass through unchanged, so every path-taking step can call this
    /// uniformly. A glob suffix (<c>payload://app/**</c>) survives the rebase
    /// and is interpreted by the step as usual.
    /// </summary>
    /// <exception cref="System.FormatException">
    /// A <c>payload://</c> path was used but no payload is available for this
    /// run, or the relative part escapes the payload root (a path-traversal
    /// attempt); or a <c>{token}</c> survived substitution (register row R16).
    /// </exception>
    public string ResolvePath(string template)
    {
        var resolved = Resolve(template);

        // R16: a brace token that never resolved must not reach the filesystem.
        // The check lives HERE, at the one resolver every path-valued step field
        // goes through, rather than in the per-step destination guards.
        // Containment legitimately varies per step — some writes deliberately land
        // outside install_dir — but "this path still contains an unresolved token"
        // never does: it is a manifest typo in `directory_create`, `run_program`,
        // `shortcut_create` and `scheduled_task_create` exactly as much as in
        // `file_copy`. Previously an unknown token was left literal, so one typo in
        // an installer.vars name silently created a directory named "{var.dest}"
        // and the install reported success. InstallEngine turns the throw into a
        // typed step failure.
        var token = BraceTokenScanner.FirstUnresolved(resolved);
        if (token is not null)
        {
            throw new System.FormatException(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"the path '{resolved}' still contains the unresolved token '{{{token}}}' after " +
                    $"substitution (from '{template}'). Refusing to use it as a path — writing it " +
                    $"verbatim would create a directory literally named '{{{token}}}'. Check the " +
                    $"spelling: an installer.vars entry must be declared before a '{{var.<name>}}' " +
                    $"token can expand."));
        }

        if (!resolved.StartsWith(PayloadScheme, System.StringComparison.Ordinal))
        {
            return resolved;
        }

        if (PayloadRoot is null)
        {
            throw new System.FormatException(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"'payload://' source used but no payload was extracted for this run: '{template}'"));
        }

        var rel = resolved[PayloadScheme.Length..]
            .Replace('/', System.IO.Path.DirectorySeparatorChar)
            .Replace('\\', System.IO.Path.DirectorySeparatorChar)
            .TrimStart(System.IO.Path.DirectorySeparatorChar);

        var rootFull = System.IO.Path.GetFullPath(PayloadRoot);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, rel));

        // Guard against '..' traversal escaping the extracted payload root.
        var rootPrefix = rootFull.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + System.IO.Path.DirectorySeparatorChar;
        if (!string.Equals(full, rootFull, System.StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            throw new System.FormatException(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"'payload://' source escapes the payload root: '{template}'"));
        }

        return full;
    }

    public bool Evaluate(string expression) =>
        _evaluator.EvaluateBool(SubstituteBraceTokens(expression), _values);
}
