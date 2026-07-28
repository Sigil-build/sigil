using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SigilBuild.Core.Configuration;

public static class ManifestParser
{
    public static ParseResult Parse(string yaml, string fileName)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0)
            {
                return new ParseResult(null, new[]
                {
                    new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.YamlSyntaxError,
                        "manifest is empty",
                        new SourceLocation(fileName, 1, 1),
                        "https://docs.sigil.build/diagnostics/SIG0001"),
                });
            }

            var root = stream.Documents[0].RootNode as YamlMappingNode
                ?? throw new YamlException("root must be a mapping");

            var diagnostics = new List<Diagnostic>();
            var manifest = MapManifest(root, fileName, diagnostics);
            return new ParseResult(manifest, diagnostics);
        }
        catch (YamlException ex)
        {
            return new ParseResult(null, new[]
            {
                new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.YamlSyntaxError,
                    ex.Message,
                    new SourceLocation(fileName, (int)ex.Start.Line, (int)ex.Start.Column),
                    "https://docs.sigil.build/diagnostics/SIG0001"),
            });
        }
    }

    private static SigilManifest MapManifest(YamlMappingNode root, string file, List<Diagnostic> diagnostics)
    {
        var loc = new SourceLocation(file, (int)root.Start.Line, (int)root.Start.Column);
        var app = MapApp(GetMapping(root, "app", required: true)!);
        // Parameters are parsed before the installer block so declared custom
        // screens (T9) can resolve their field references against them.
        var parameters = ParseParameters(GetMapping(root, "parameters"), diagnostics, file);
        var installer = MapInstaller(GetMapping(root, "installer"), app, parameters, diagnostics, file);
        // P11: installer.scope is fully resolved by MapInstaller above (default
        // Auto when the block is absent), so every root-level step collection
        // parsed below can thread it straight into ParseInstallStep, which
        // guards each step (SIG0310) at its own precise node location — see
        // MachineScopeGuard.
        var scope = installer?.Scope ?? InstallScope.Auto;
        var installSteps = ParseInstallSteps(GetSequenceOfMappings(root, "install_steps"), scope, diagnostics, file);
        var preInstall = ParseInstallSteps(GetSequenceOfMappings(root, "pre_install"), scope, diagnostics, file);
        var postInstall = ParseInstallSteps(GetSequenceOfMappings(root, "post_install"), scope, diagnostics, file);
        var uninstall = ParseInstallSteps(GetSequenceOfMappings(root, "uninstall"), scope, diagnostics, file);

        return new SigilManifest(
            Spec: GetScalar(root, "spec") ?? "",
            App: app,
            Build: MapBuild(GetMapping(root, "build", required: true)!),
            Package: MapPackage(GetMapping(root, "package")),
            Sign: MapSign(GetMapping(root, "sign")),
            Publish: MapPublish(GetMapping(root, "publish")),
            Updates: MapUpdates(GetMapping(root, "updates")),
            Installer: installer,
            Location: loc,
            Parameters: parameters,
            InstallSteps: installSteps,
            PreInstall: preInstall,
            PostInstall: postInstall,
            Uninstall: uninstall);
    }

    private static AppSection MapApp(YamlMappingNode node) => new(
        Id: GetScalar(node, "id") ?? "",
        Name: GetScalar(node, "name") ?? "",
        Version: GetScalar(node, "version") ?? "",
        Publisher: GetScalar(node, "publisher") ?? "",
        Description: GetScalar(node, "description"),
        Homepage: GetScalar(node, "homepage"));

    private static BuildSection MapBuild(YamlMappingNode node) => new(
        Source: GetScalar(node, "source") ?? "",
        Include: GetSequence(node, "include"),
        Exclude: GetSequence(node, "exclude"),
        Deterministic: GetBool(node, "deterministic", defaultValue: true));

    private static PackageSection? MapPackage(YamlMappingNode? node)
    {
        if (node is null) return null;
        var formats = GetSequence(node, "formats")?.Select(ParseFormat).ToArray()
            ?? new[] { PackageFormat.Zip };
        var arches = GetSequence(node, "architectures")?.Select(ParseArch).ToArray()
            ?? new[] { TargetArchitecture.X64 };
        var msix = GetMapping(node, "msix");
        return new PackageSection(formats, arches, msix is null ? null : new MsixOptions(
            Publisher: GetScalar(msix, "publisher"),
            Logo: GetScalar(msix, "logo"),
            Capabilities: GetSequence(msix, "capabilities"),
            RunWack: GetBool(msix, "runWack", defaultValue: false)));
    }

    private static SignSection? MapSign(YamlMappingNode? node)
    {
        if (node is null) return null;
        var provider = GetScalar(node, "provider") switch
        {
            "local" => SignProvider.Local,
            "azure-trusted-signing" => SignProvider.AzureTrustedSigning,
            _ => SignProvider.None,
        };
        var local = GetMapping(node, "local");
        var azure = GetMapping(node, "azureTrustedSigning");
        return new SignSection(
            provider,
            local is null ? null : new LocalSignConfig(
                Pfx: GetScalar(local, "pfx") ?? "",
                PasswordEnv: GetScalar(local, "passwordEnv"),
                TimestampUrl: GetScalar(local, "timestampUrl") ?? "http://timestamp.digicert.com"),
            azure is null ? null : new AzureTrustedSigningConfig(
                Endpoint: GetScalar(azure, "endpoint") ?? "",
                AccountName: GetScalar(azure, "accountName") ?? "",
                CertificateProfile: GetScalar(azure, "certificateProfile") ?? "",
                TenantIdEnv: GetScalar(azure, "tenantIdEnv") ?? "AZURE_TENANT_ID",
                ClientIdEnv: GetScalar(azure, "clientIdEnv") ?? "AZURE_CLIENT_ID",
                ClientSecretEnv: GetScalar(azure, "clientSecretEnv") ?? "AZURE_CLIENT_SECRET"));
    }

    private static PublishSection? MapPublish(YamlMappingNode? node)
    {
        if (node is null) return null;
        var gh = GetMapping(node, "github");
        return new PublishSection(gh is null ? null : new GitHubPublishConfig(
            Repo: GetScalar(gh, "repo") ?? "",
            TagPrefix: GetScalar(gh, "tagPrefix") ?? "v",
            Draft: GetBool(gh, "draft", defaultValue: false)));
    }

    private static UpdatesSection? MapUpdates(YamlMappingNode? node)
    {
        if (node is null) return null;
        return new UpdatesSection(
            Channel: GetScalar(node, "channel") ?? "stable",
            ManifestUrl: GetScalar(node, "manifestUrl"),
            DeltaTargets: GetInt(node, "deltaTargets", defaultValue: 3),
            SigningKey: GetScalar(node, "signingKey"));
    }

    private static InstallerSection? MapInstaller(
        YamlMappingNode? node,
        AppSection app,
        IReadOnlyDictionary<string, ParameterDefinition>? parameters,
        List<Diagnostic> diagnostics,
        string fileName)
    {
        if (node is null) return null;
        var loc = new SourceLocation(fileName, (int)node.Start.Line, (int)node.Start.Column);
        var brand = GetMapping(node, "brand");
        var screens = ParseScreens(
            GetSequenceOfMappings(node, "screens"), app, parameters, diagnostics, fileName);
        // P11: resolve scope before installer.hooks is parsed so each hook step
        // can be guarded (SIG0310) at its own precise node location, same as the
        // root-level step collections in MapManifest.
        var scope = ParseScope(node, diagnostics, fileName);
        return new InstallerSection(
            brand is null ? null : new InstallerBrand(
                Logo: GetScalar(brand, "logo"),
                Hero: GetScalar(brand, "hero"),
                PrimaryColor: GetScalar(brand, "primaryColor"),
                AccentColor: GetScalar(brand, "accentColor")),
            // T8: built-in configurable components (desktop_shortcut, start_menu,
            // add_to_path, file_associations). Each is a shorthand true/false or an
            // object { enabled, default, locked, ...component keys }. Pack time turns
            // each ENABLED component into its gated install step(s).
            Options: ParseOptions(GetMapping(node, "options"), parameters, diagnostics, fileName),
            Screens: screens,
            // T14 / P9 (gap G10): capture the license path(s) only, as a
            // LocalizedText — a plain string or a `{en: ..., uk: ...}` map of
            // per-language file paths, through the same ParseLocalizedText path
            // as title/subtitle/description. The actual file read + embed
            // happens at PACK time (ExeWrapperPackager.ReadLicenseText), which
            // resolves each path against the pack source dir and emits SIG0250
            // (non-fatal, per entry) / SIG0290 (fatal, on the post-read map) —
            // see design §5.3. Retyping this from `string?` closes a silent-null
            // window: previously GetScalar returned null with zero diagnostic for
            // any manifest that declared `license:` as a map, and the License
            // screen would vanish without a trace.
            License: ParseLocalizedText(node, "license", loc, diagnostics),
            // T12: install scope (user | machine | auto, default auto). The schema
            // enum is the hard gate; here we map the string leniently and emit a
            // non-fatal diagnostic on an unrecognized value, falling back to auto.
            Scope: scope,
            // T13: optional install-dir override. Captured verbatim as a template;
            // the engine resolves its {scope_root} / {app.*} tokens at install time
            // (StepContext), against the resolved scope. A blank value is treated as
            // absent so the default (<scope root>\<App.Name>) applies.
            InstallDir: string.IsNullOrWhiteSpace(GetScalar(node, "install_dir"))
                ? null
                : GetScalar(node, "install_dir"),
            // PR #8: optional custom installer-exe icon (.ico) path. Null falls
            // back to the bundled default installer icon at pack time.
            Icon: GetScalar(node, "icon"),
            // P1 (gap G1): declarative variables. Each is `name: <expression>`;
            // evaluated once at install-session start, in dependency order,
            // exposed as var.<name>. Cycles/malformed expressions are diagnosed
            // here (SIG0270) so a broken manifest fails the pack.
            Vars: ParseVars(GetMapping(node, "vars"), diagnostics, fileName),
            // P2 (gap G2): lifecycle hooks that run OUTSIDE the rollback journal.
            // Per-phase on_failure defaults: fail for pre_*, continue for post_*.
            Hooks: ParseHooks(GetMapping(node, "hooks"), scope, diagnostics, fileName),
            // P2 (gap G4): the Done-screen "Launch <App>" target.
            RunAfterInstall: ParseRunAfterInstall(GetMapping(node, "run_after_install")),
            // P5 (gap G6): first-class prerequisite units (detect → install → re-detect),
            // run before the journaled body. An https source without a sha256 is refused here.
            Prerequisites: ParsePrerequisites(GetSequenceOfMappings(node, "prerequisites"), diagnostics, fileName),
            // P6 (gap G7): named mutexes the running app holds; setup probes them
            // before touching the install dir (Inno AppMutex equivalent).
            AppMutex: GetSequence(node, "app_mutex"),
            // P9 (gap G10): optional fixed installer language. Stored verbatim
            // (schema is permissive); an invalid tag is diagnosed (SIG0291) but
            // otherwise doesn't block the parse — the resolver chain simply
            // won't match a value that fails LanguageTag.IsValid.
            Language: ParseInstallerLanguage(node, diagnostics, fileName));
    }

    /// <summary>
    /// Parse the manifest's <c>installer.language</c> scalar (P9, gap G10): the
    /// first link in the language-preference chain (installer.language -&gt; /lang
    /// -&gt; OS list -&gt; en). Emits <see cref="DiagnosticCodes.InvalidLanguageTag"/>
    /// (SIG0291, Error) when present but not a valid BCP-47-subset tag per
    /// <see cref="LanguageTag.IsValid"/> — the same rule the <c>/lang</c> flag uses.
    /// </summary>
    private static string? ParseInstallerLanguage(
        YamlMappingNode node, List<Diagnostic> diagnostics, string fileName)
    {
        var raw = GetScalar(node, "language");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!LanguageTag.IsValid(raw))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.InvalidLanguageTag,
                $"installer.language '{raw}' is not a valid language tag",
                new SourceLocation(fileName, (int)node.Start.Line, (int)node.Start.Column),
                "https://docs.sigil.build/diagnostics/SIG0291"));
        }

        return raw;
    }

    /// <summary>
    /// Parse a manifest field that may be authored either as a plain string or as
    /// a <c>{ en: ..., uk: ... }</c> map (P9, gap G10 — <see cref="LocalizedText"/>).
    /// A plain string normalizes to <c>{"en": value}</c>. A map is carried
    /// verbatim; each key is validated as a language tag
    /// (<see cref="DiagnosticCodes.InvalidLanguageTag"/>, SIG0291) and the whole
    /// map must contain an <c>en</c> entry
    /// (<see cref="DiagnosticCodes.LocalizedTextMissingEnglish"/>, SIG0290 —
    /// fatal, since every runtime fallback bottoms out at English). A per-language
    /// value that isn't a plain scalar (e.g. a nested sequence) is diagnosed
    /// (<see cref="DiagnosticCodes.LocalizedTextValueNotScalar"/>, SIG0292) rather
    /// than silently collapsing to <c>""</c>. Returns <c>null</c> when the key is
    /// absent.
    /// </summary>
    private static LocalizedText? ParseLocalizedText(
        YamlMappingNode parent, string key, SourceLocation loc, List<Diagnostic> diagnostics)
    {
        if (!parent.Children.TryGetValue(new YamlScalarNode(key), out var node))
        {
            return null;
        }

        if (node is YamlScalarNode scalar)
        {
            return LocalizedText.Plain(scalar.Value ?? string.Empty);
        }

        if (node is not YamlMappingNode map)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in map.Children)
        {
            if (kvp.Key is not YamlScalarNode tagNode || tagNode.Value is null)
            {
                continue;
            }

            var tag = tagNode.Value;
            if (kvp.Value is YamlScalarNode textNode)
            {
                values[tag] = textNode.Value ?? string.Empty;
            }
            else
            {
                // Silent-drop guard: a non-scalar value (e.g. a nested sequence
                // or mapping under a language key) used to collapse to "" here
                // with zero diagnostic — the same silent-blank-rendering shape
                // SIG0290 exists to prevent, just one language key at a time.
                values[tag] = string.Empty;
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.LocalizedTextValueNotScalar,
                    $"'{key}.{tag}' must be a plain string; found a non-scalar value instead",
                    loc,
                    "https://docs.sigil.build/diagnostics/SIG0292"));
            }

            if (!LanguageTag.IsValid(tag))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.InvalidLanguageTag,
                    $"'{key}' has an invalid language tag '{tag}'",
                    loc,
                    "https://docs.sigil.build/diagnostics/SIG0291"));
            }
        }

        var localizedText = new LocalizedText(values);
        if (!localizedText.HasEnglish)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.LocalizedTextMissingEnglish,
                $"'{key}' is missing an 'en' entry — every runtime fallback bottoms out at English",
                loc,
                "https://docs.sigil.build/diagnostics/SIG0290"));
        }

        return localizedText;
    }

    /// <summary>
    /// Parse the <c>installer.prerequisites</c> block (P5, gap G6). Each entry needs a
    /// <c>name</c>, a <c>detect</c> expression, and a <c>source</c> (<c>payload://</c> or
    /// <c>https://</c>); an <c>https://</c> source additionally requires a <c>sha256</c>
    /// integrity checksum (a download without one is refused — SIG0280). Optional
    /// <c>args</c>, <c>exit_codes_ok</c> (default <c>[0]</c>), <c>scope_required</c>
    /// (<c>allusers</c>|<c>currentuser</c>), and <c>timeout_seconds</c>. A malformed entry
    /// is diagnosed and skipped; returns <c>null</c> when nothing usable is declared.
    /// </summary>
    private static List<InstallerPrerequisite>? ParsePrerequisites(
        List<YamlMappingNode>? nodes, List<Diagnostic> diagnostics, string fileName)
    {
        if (nodes is null || nodes.Count == 0) return null;

        var list = new List<InstallerPrerequisite>(nodes.Count);
        foreach (var node in nodes)
        {
            var loc = new SourceLocation(fileName, (int)node.Start.Line, (int)node.Start.Column);
            var name = GetScalar(node, "name");
            var detect = GetScalar(node, "detect");
            var source = GetScalar(node, "source");
            var sha256 = GetScalar(node, "sha256");

            if (string.IsNullOrWhiteSpace(name))
            {
                AddPrereqError(diagnostics, loc, "installer.prerequisites entry is missing a 'name'");
                continue;
            }
            if (string.IsNullOrWhiteSpace(detect))
            {
                AddPrereqError(diagnostics, loc, $"installer.prerequisites '{name}' is missing a 'detect' expression");
                continue;
            }
            // Structural check on the detect expression (balanced parens/brackets,
            // terminated string literals) — the same gross-malformation gate as
            // installer.vars. The full grammar is checked by the engine at install time.
            if (!TryValidateVarStructure(detect, out var detectReason))
            {
                AddPrereqError(diagnostics, loc, $"installer.prerequisites '{name}' detect expression is invalid ({detectReason}): '{detect}'");
                continue;
            }
            if (string.IsNullOrWhiteSpace(source))
            {
                AddPrereqError(diagnostics, loc, $"installer.prerequisites '{name}' is missing a 'source' (payload:// or https://)");
                continue;
            }

            var isHttps = source.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase);
            var isPayload = source.StartsWith("payload://", System.StringComparison.OrdinalIgnoreCase);
            if (source.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase))
            {
                AddPrereqError(diagnostics, loc, $"installer.prerequisites '{name}' source must be https:// (got '{source}')");
                continue;
            }
            if (!isHttps && !isPayload)
            {
                AddPrereqError(diagnostics, loc, $"installer.prerequisites '{name}' source must be a payload:// or https:// URL (got '{source}')");
                continue;
            }
            // sha256 REQUIRED for https (a download without an integrity check is refused);
            // ignored for payload:// where integrity comes from the signed package.
            if (isHttps && string.IsNullOrWhiteSpace(sha256))
            {
                AddPrereqError(diagnostics, loc, $"installer.prerequisites '{name}' has an https:// source and must declare a 'sha256' — a download without an integrity checksum is refused");
                continue;
            }

            string? scopeRequired = null;
            var rawScope = GetScalar(node, "scope_required");
            if (!string.IsNullOrWhiteSpace(rawScope))
            {
                var norm = rawScope.Trim().ToLowerInvariant();
                if (norm is "allusers" or "currentuser")
                {
                    scopeRequired = norm;
                }
                else
                {
                    AddPrereqError(diagnostics, loc, $"installer.prerequisites '{name}' scope_required must be 'allusers' or 'currentuser' (got '{rawScope}')");
                    continue;
                }
            }

            list.Add(new InstallerPrerequisite(
                Name: name,
                Detect: detect,
                Source: source,
                Sha256: string.IsNullOrWhiteSpace(sha256) ? null : sha256,
                Args: GetSequence(node, "args"),
                ExitCodesOk: GetIntSequence(node, "exit_codes_ok"),
                ScopeRequired: scopeRequired,
                TimeoutSeconds: GetNullableInt(node, "timeout_seconds")));
        }

        return list.Count == 0 ? null : list;
    }

    private static void AddPrereqError(List<Diagnostic> diagnostics, SourceLocation loc, string message)
        => diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            DiagnosticCodes.InvalidPrerequisite,
            message,
            loc,
            "https://docs.sigil.build/diagnostics/SIG0280"));

    /// <summary>
    /// Parse the <c>installer.hooks</c> block (P2). Each phase reuses the ordinary
    /// step parser but with a phase-specific default <c>on_failure</c>: <c>fail</c>
    /// for the pre_* phases (a failed pre-hook aborts before the journal opens /
    /// before the uninstall replays) and <c>continue</c> for the post_* phases (the
    /// install is committed and cannot be rolled back). Returns <c>null</c> when the
    /// block declares no phase.
    /// </summary>
    private static InstallerHooks? ParseHooks(
        YamlMappingNode? node, InstallScope scope, List<Diagnostic> diagnostics, string fileName)
    {
        if (node is null) return null;

        var pre = ParseInstallSteps(GetSequenceOfMappings(node, "pre_install"), scope, diagnostics, fileName, OnFailure.Fail);
        var post = ParseInstallSteps(GetSequenceOfMappings(node, "post_install"), scope, diagnostics, fileName, OnFailure.Continue);
        var preU = ParseInstallSteps(GetSequenceOfMappings(node, "pre_uninstall"), scope, diagnostics, fileName, OnFailure.Fail);
        var postU = ParseInstallSteps(GetSequenceOfMappings(node, "post_uninstall"), scope, diagnostics, fileName, OnFailure.Continue);

        if (pre is null && post is null && preU is null && postU is null)
        {
            return null;
        }
        return new InstallerHooks(pre, post, preU, postU);
    }

    /// <summary>
    /// Parse the <c>installer.run_after_install</c> block (P2): a required
    /// <c>path</c> and optional <c>args</c>. Returns <c>null</c> when absent or the
    /// path is blank (the Done screen then shows no launch checkbox).
    /// </summary>
    private static RunAfterInstall? ParseRunAfterInstall(YamlMappingNode? node)
    {
        if (node is null) return null;
        var path = GetScalar(node, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return new RunAfterInstall(path, GetSequence(node, "args"));
    }

    /// <summary>
    /// Parse the manifest's <c>installer.vars</c> block (P1). Each entry is
    /// <c>name: &lt;expression&gt;</c>. Emits <see cref="DiagnosticCodes.InvalidInstallerVar"/>
    /// (SIG0270, Error) for a non-scalar/empty value, a grossly malformed
    /// expression, or a reference cycle among the vars. Declaration order is
    /// preserved (deterministic packaging); dependency order is resolved later by
    /// <see cref="InstallerVarGraph"/>. Returns <c>null</c> when the block is
    /// absent or declares nothing usable.
    /// </summary>
    private static List<InstallerVar>? ParseVars(
        YamlMappingNode? node, List<Diagnostic> diagnostics, string fileName)
    {
        if (node is null) return null;

        var list = new List<InstallerVar>(node.Children.Count);
        foreach (var kvp in node.Children)
        {
            if (kvp.Key is not YamlScalarNode keyNode) continue;
            var name = keyNode.Value ?? string.Empty;
            var loc = new SourceLocation(fileName, (int)keyNode.Start.Line, (int)keyNode.Start.Column);

            if (string.IsNullOrWhiteSpace(name)) continue;

            if (kvp.Value is not YamlScalarNode exprNode || string.IsNullOrWhiteSpace(exprNode.Value))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.InvalidInstallerVar,
                    $"installer.vars '{name}' must be a non-empty expression string",
                    loc,
                    "https://docs.sigil.build/diagnostics/SIG0270"));
                continue;
            }

            var expr = exprNode.Value;
            if (!TryValidateVarStructure(expr, out var reason))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.InvalidInstallerVar,
                    $"installer.vars '{name}' expression is invalid ({reason}): '{expr}'",
                    loc,
                    "https://docs.sigil.build/diagnostics/SIG0270"));
                continue;
            }

            list.Add(new InstallerVar(name, expr));
        }

        if (list.Count == 0) return null;

        // Structural cycle check (name-based). A cycle has no safe evaluation
        // order, so it is fatal — the pack fails rather than emitting a blob that
        // would loop at install time.
        try
        {
            InstallerVarGraph.TopologicalOrder(list);
        }
        catch (InstallerVarCycleException ex)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.InvalidInstallerVar,
                $"installer.vars form a reference cycle: {string.Join(" -> ", ex.Cycle)}",
                new SourceLocation(fileName, (int)node.Start.Line, (int)node.Start.Column),
                "https://docs.sigil.build/diagnostics/SIG0270"));
        }

        return list;
    }

    /// <summary>
    /// Gross-malformation check for a <c>installer.vars</c> expression: balanced
    /// parentheses/brackets, terminated string literals (single OR double quoted,
    /// matching the lexer), and non-empty. Unlike the screen <c>when</c> validator
    /// this does NOT restrict the character set outside strings — registry/file
    /// paths carry backslashes, colons, and spaces inside string literals, and the
    /// full grammar is checked at install time by the Wrapper.Core engine.
    /// </summary>
    private static bool TryValidateVarStructure(string expr, out string? reason)
    {
        var depthParen = 0;
        var depthBracket = 0;
        var sawContent = false;
        var i = 0;
        var n = expr.Length;
        while (i < n)
        {
            var c = expr[i];
            if (c is '\'' or '"')
            {
                sawContent = true;
                var quote = c;
                i++;
                while (i < n && expr[i] != quote) i++;
                if (i >= n) { reason = "unterminated string literal"; return false; }
                i++; // consume closing quote
                continue;
            }

            switch (c)
            {
                case '(': depthParen++; break;
                case ')':
                    depthParen--;
                    if (depthParen < 0) { reason = "unbalanced parentheses"; return false; }
                    break;
                case '[': depthBracket++; break;
                case ']':
                    depthBracket--;
                    if (depthBracket < 0) { reason = "unbalanced brackets"; return false; }
                    break;
                default:
                    if (!char.IsWhiteSpace(c)) sawContent = true;
                    break;
            }

            i++;
        }

        if (depthParen != 0) { reason = "unbalanced parentheses"; return false; }
        if (depthBracket != 0) { reason = "unbalanced brackets"; return false; }
        if (!sawContent) { reason = "empty expression"; return false; }

        reason = null;
        return true;
    }

    /// <summary>
    /// Map the manifest's <c>installer.scope</c> scalar into
    /// <see cref="InstallScope"/> (T12). Recognizes <c>user</c> / <c>machine</c> /
    /// <c>auto</c> case-insensitively; an absent value defaults to
    /// <see cref="InstallScope.Auto"/>; an out-of-enum value falls back to
    /// <see cref="InstallScope.Auto"/> with a non-fatal
    /// <see cref="DiagnosticCodes.InvalidInstallerScope"/> diagnostic.
    /// </summary>
    private static InstallScope ParseScope(
        YamlMappingNode node, List<Diagnostic> diagnostics, string fileName)
    {
        var raw = GetScalar(node, "scope");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return InstallScope.Auto;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "user": return InstallScope.User;
            case "machine": return InstallScope.Machine;
            case "auto": return InstallScope.Auto;
            default:
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    DiagnosticCodes.InvalidInstallerScope,
                    $"installer.scope '{raw}' is not one of user|machine|auto — defaulting to auto",
                    new SourceLocation(fileName, (int)node.Start.Line, (int)node.Start.Column),
                    "https://docs.sigil.build/diagnostics/SIG0260"));
                return InstallScope.Auto;
        }
    }

    /// <summary>
    /// Parse the manifest's <c>installer.options</c> block (T8). Each of the four
    /// built-in components (<c>desktop_shortcut</c>, <c>start_menu</c>,
    /// <c>add_to_path</c>, <c>file_associations</c>) is either a shorthand boolean
    /// or an object <c>{ enabled, default, locked, ...component keys }</c>. The
    /// shorthand maps onto the M0 records: <c>true</c> →
    /// <c>{ Enabled = true, Default = true }</c>; <c>false</c> →
    /// <c>{ Enabled = false }</c>. An absent component stays <c>null</c> (its
    /// built-in default: not declared, so nothing is generated). Returns
    /// <c>null</c> when the block is absent or declares no component.
    /// </summary>
    private static InstallerOptions? ParseOptions(
        YamlMappingNode? node,
        IReadOnlyDictionary<string, ParameterDefinition>? parameters,
        List<Diagnostic> diagnostics,
        string fileName)
    {
        if (node is null) return null;

        var desktop = ParseBoolOption(node, "desktop_shortcut");
        var startMenu = ParseBoolOption(node, "start_menu");
        var addToPath = ParseBoolOption(node, "add_to_path");
        var fileAssoc = ParseFileAssociationOption(node, "file_associations");
        var components = ParseCustomComponents(
            GetSequenceOfMappings(node, "components"), parameters, diagnostics, fileName);

        if (desktop is null && startMenu is null && addToPath is null && fileAssoc is null
            && components is null)
        {
            return null;
        }

        return new InstallerOptions(desktop, startMenu, addToPath, fileAssoc, components);
    }

    // The four built-in component keys — reserved: a custom component may not
    // reuse one (its own generated step would collide with the built-in's gate).
    private static readonly HashSet<string> BuiltInComponentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop_shortcut", "start_menu", "add_to_path", "file_associations",
    };

    /// <summary>
    /// Parse the <c>installer.options.components[]</c> sequence (P10, gap G11) —
    /// app-defined custom components. Each entry is
    /// <c>{ name, label, description?, default?, locked?, when? }</c>. Emits
    /// <see cref="DiagnosticCodes.InvalidCustomComponent"/> (SIG0300, Error) for a
    /// name that is not a bare identifier, collides with a built-in component or a
    /// declared parameter, duplicates another custom component, or a component that
    /// omits its required <c>label</c>. Declaration order is preserved. Returns
    /// <c>null</c> when the sequence is absent or yields nothing usable.
    /// </summary>
    private static List<CustomComponent>? ParseCustomComponents(
        List<YamlMappingNode>? nodes,
        IReadOnlyDictionary<string, ParameterDefinition>? parameters,
        List<Diagnostic> diagnostics,
        string fileName)
    {
        if (nodes is null) return null;

        var list = new List<CustomComponent>(nodes.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var loc = new SourceLocation(fileName, (int)node.Start.Line, (int)node.Start.Column);
            var name = GetScalar(node, "name") ?? string.Empty;

            void Invalid(string message) => diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.InvalidCustomComponent,
                message,
                loc,
                "https://docs.sigil.build/diagnostics/SIG0300"));

            if (string.IsNullOrWhiteSpace(name))
            {
                Invalid("installer.options.components[] entry is missing a 'name'");
                continue;
            }

            var valid = true;
            if (!IsBareIdentifier(name))
            {
                Invalid($"custom component name '{name}' must be a bare identifier ([A-Za-z_][A-Za-z0-9_]*)");
                valid = false;
            }
            if (BuiltInComponentNames.Contains(name))
            {
                Invalid($"custom component name '{name}' collides with a built-in component");
                valid = false;
            }
            if (parameters is not null && parameters.ContainsKey(name))
            {
                Invalid($"custom component name '{name}' collides with a declared parameter");
                valid = false;
            }
            if (!seen.Add(name))
            {
                Invalid($"custom component name '{name}' is declared more than once");
                valid = false;
            }

            var label = ParseLocalizedText(node, "label", loc, diagnostics);
            if (label is null)
            {
                Invalid($"custom component '{name}' requires a 'label'");
                valid = false;
            }

            if (!valid || label is null)
            {
                continue;
            }

            var description = ParseLocalizedText(node, "description", loc, diagnostics);
            var when = GetScalar(node, "when");
            list.Add(new CustomComponent(
                Name: name,
                Label: label,
                Default: GetBool(node, "default", defaultValue: false),
                Locked: GetBool(node, "locked", defaultValue: false),
                Description: description,
                When: string.IsNullOrWhiteSpace(when) ? null : when));
        }

        return list.Count == 0 ? null : list;
    }

    /// <summary>A bare (dot-free) identifier as the expression lexer accepts one: <c>[A-Za-z_][A-Za-z0-9_]*</c>.</summary>
    private static bool IsBareIdentifier(string s)
    {
        if (s.Length == 0) return false;
        if (!(char.IsAsciiLetter(s[0]) || s[0] == '_')) return false;
        for (var i = 1; i < s.Length; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) return false;
        }
        return true;
    }

    /// <summary>
    /// Parse a single boolean-shaped option component (<c>desktop_shortcut</c> /
    /// <c>start_menu</c> / <c>add_to_path</c>): shorthand boolean or the
    /// <c>{ enabled, default, locked }</c> object. Returns <c>null</c> when the key
    /// is absent.
    /// </summary>
    private static InstallerOption? ParseBoolOption(YamlMappingNode parent, string key)
    {
        if (!parent.Children.TryGetValue(new YamlScalarNode(key), out var node))
        {
            return null;
        }

        return node switch
        {
            YamlScalarNode s when bool.TryParse(s.Value, out var b) =>
                b ? new InstallerOption(Enabled: true, Default: true)
                  : new InstallerOption(Enabled: false),
            YamlMappingNode m => new InstallerOption(
                Enabled: GetBool(m, "enabled", defaultValue: true),
                Default: GetBool(m, "default", defaultValue: true),
                Locked: GetBool(m, "locked", defaultValue: false)),
            _ => null,
        };
    }

    /// <summary>
    /// Parse the <c>file_associations</c> component: shorthand boolean or the
    /// <c>{ enabled, default, locked, extensions: [".x"] }</c> object. Returns
    /// <c>null</c> when the key is absent.
    /// </summary>
    private static FileAssociationOption? ParseFileAssociationOption(YamlMappingNode parent, string key)
    {
        if (!parent.Children.TryGetValue(new YamlScalarNode(key), out var node))
        {
            return null;
        }

        return node switch
        {
            YamlScalarNode s when bool.TryParse(s.Value, out var b) =>
                b ? new FileAssociationOption(Enabled: true, Default: true)
                  : new FileAssociationOption(Enabled: false),
            YamlMappingNode m => new FileAssociationOption(
                Enabled: GetBool(m, "enabled", defaultValue: true),
                Default: GetBool(m, "default", defaultValue: true),
                Locked: GetBool(m, "locked", defaultValue: false),
                Extensions: GetSequence(m, "extensions")),
            _ => null,
        };
    }

    // Interpolation tokens permitted in a screen Title / Subtitle (T9). Kept in
    // sync with the substitution surface the wizard resolves at render time.
    private static readonly string[] KnownScreenTokens =
        { "app.name", "app.id", "app.version", "app.publisher" };

    private static List<InstallerScreen>? ParseScreens(
        List<YamlMappingNode>? nodes,
        AppSection app,
        IReadOnlyDictionary<string, ParameterDefinition>? parameters,
        List<Diagnostic> diagnostics,
        string fileName)
    {
        _ = app; // App is available for future token-value validation; tokens are name-checked below.
        if (nodes is null) return null;
        var list = new List<InstallerScreen>(nodes.Count);
        foreach (var node in nodes)
        {
            var loc = new SourceLocation(fileName, (int)node.Start.Line, (int)node.Start.Column);
            var id = GetScalar(node, "id") ?? string.Empty;
            var title = ParseLocalizedText(node, "title", loc, diagnostics) ?? LocalizedText.Plain(string.Empty);
            var subtitle = ParseLocalizedText(node, "subtitle", loc, diagnostics);
            var when = GetScalar(node, "when");

            foreach (var value in title.Values.Values)
            {
                ValidateInterpolationTokens(value, loc, diagnostics);
            }
            if (subtitle is not null)
            {
                foreach (var value in subtitle.Values.Values)
                {
                    ValidateInterpolationTokens(value, loc, diagnostics);
                }
            }

            if (!string.IsNullOrWhiteSpace(when))
            {
                ValidateWhenExpression(when!, loc, diagnostics);
            }

            var fields = ParseScreenFields(node, parameters, diagnostics, loc);
            list.Add(new InstallerScreen(id, title, subtitle, when, fields));
        }
        return list;
    }

    private static List<ScreenField> ParseScreenFields(
        YamlMappingNode screenNode,
        IReadOnlyDictionary<string, ParameterDefinition>? parameters,
        List<Diagnostic> diagnostics,
        SourceLocation loc)
    {
        var fields = new List<ScreenField>();
        if (!screenNode.Children.TryGetValue(new YamlScalarNode("fields"), out var node)
            || node is not YamlSequenceNode seq)
        {
            return fields;
        }

        foreach (var child in seq.Children)
        {
            string? param;
            string? widget = null;
            switch (child)
            {
                case YamlScalarNode s:
                    param = s.Value;
                    break;
                case YamlMappingNode m:
                    param = GetScalar(m, "param");
                    widget = GetScalar(m, "widget");
                    break;
                default:
                    continue;
            }

            if (string.IsNullOrEmpty(param))
            {
                continue;
            }

            if (parameters is null || !parameters.ContainsKey(param))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.UnknownScreenParameterRef,
                    $"installer screen field references unknown parameter '{param}' — declare it under the top-level 'parameters:' block",
                    loc,
                    "https://docs.sigil.build/diagnostics/SIG0240"));
            }

            fields.Add(new ScreenField(param, widget));
        }

        return fields;
    }

    private static void ValidateInterpolationTokens(
        string text, SourceLocation loc, List<Diagnostic> diagnostics)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{')
            {
                i++;
                continue;
            }

            var end = text.IndexOf('}', i + 1);
            if (end < 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.InvalidScreenTitleToken,
                    $"installer screen title/subtitle has an unterminated '{{' token: '{text}'",
                    loc,
                    "https://docs.sigil.build/diagnostics/SIG0242"));
                return;
            }

            var token = text.Substring(i + 1, end - i - 1).Trim();
            var known = false;
            foreach (var t in KnownScreenTokens)
            {
                if (string.Equals(t, token, StringComparison.Ordinal)) { known = true; break; }
            }
            if (!known)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.InvalidScreenTitleToken,
                    $"installer screen title/subtitle references unknown interpolation token '{{{token}}}' (allowed: {string.Join(", ", KnownScreenTokens)})",
                    loc,
                    "https://docs.sigil.build/diagnostics/SIG0242"));
            }

            i = end + 1;
        }
    }

    // Lightweight structural validation of a screen `when` expression. The full
    // grammar is evaluated at install time by the Wrapper.Core expression engine;
    // SigilBuild.Core cannot reference that engine without a layering cycle
    // (Wrapper.Core → Core), so this catches gross malformations (empty text,
    // unbalanced parentheses/brackets, illegal characters) at pack/validate time.
    private static void ValidateWhenExpression(
        string when, SourceLocation loc, List<Diagnostic> diagnostics)
    {
        void Report(string reason) => diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            DiagnosticCodes.InvalidScreenWhenExpression,
            $"installer screen 'when' expression is invalid ({reason}): '{when}'",
            loc,
            "https://docs.sigil.build/diagnostics/SIG0241"));

        var depthParen = 0;
        var depthBracket = 0;
        var inString = false;
        var sawContent = false;
        foreach (var c in when)
        {
            if (inString)
            {
                sawContent = true;
                if (c == '"') { inString = false; }
                continue;
            }

            switch (c)
            {
                case '"': inString = true; sawContent = true; break;
                case '(': depthParen++; break;
                case ')':
                    depthParen--;
                    if (depthParen < 0) { Report("unbalanced parentheses"); return; }
                    break;
                case '[': depthBracket++; break;
                case ']':
                    depthBracket--;
                    if (depthBracket < 0) { Report("unbalanced brackets"); return; }
                    break;
                default:
                    if (char.IsLetterOrDigit(c) || "._!=<>&|,-+ \t'".Contains(c, StringComparison.Ordinal))
                    {
                        if (!char.IsWhiteSpace(c)) { sawContent = true; }
                    }
                    else
                    {
                        Report($"illegal character '{c}'");
                        return;
                    }
                    break;
            }
        }

        if (inString) { Report("unterminated string literal"); return; }
        if (depthParen != 0) { Report("unbalanced parentheses"); return; }
        if (depthBracket != 0) { Report("unbalanced brackets"); return; }
        if (!sawContent) { Report("empty expression"); }
    }

    private static Dictionary<string, ParameterDefinition>? ParseParameters(
        YamlMappingNode? node, List<Diagnostic> diagnostics, string fileName)
    {
        if (node is null) return null;
        var dict = new Dictionary<string, ParameterDefinition>(StringComparer.Ordinal);
        foreach (var kvp in node.Children)
        {
            if (kvp.Key is not YamlScalarNode keyNode || kvp.Value is not YamlMappingNode value)
                continue;

            var name = keyNode.Value ?? string.Empty;
            var typeStr = GetScalar(value, "type");
            if (!TryParseParameterType(typeStr, out var type))
            {
                var loc = new SourceLocation(fileName, (int)keyNode.Start.Line, (int)keyNode.Start.Column);
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.UnknownParameterType,
                    $"unknown parameter type '{typeStr}' for parameter '{name}'",
                    loc,
                    "https://docs.sigil.build/diagnostics/SIG0210"));
                continue;
            }

            var values = GetSequence(value, "values");
            var installTime = GetBool(value, "install_time", defaultValue: false);
            var descriptionLoc = new SourceLocation(fileName, (int)keyNode.Start.Line, (int)keyNode.Start.Column);
            var description = ParseLocalizedText(value, "description", descriptionLoc, diagnostics);
            var pattern = GetScalar(value, "pattern");
            var min = GetNullableInt(value, "min");
            var max = GetNullableInt(value, "max");
            var screen = GetScalar(value, "screen");
            var defaultValue = ReadDefault(value, type);

            var sourceMap = GetMapping(value, "source");
            ParameterSource? source = null;
            if (sourceMap is not null)
            {
                var paramLoc = new SourceLocation(fileName, (int)keyNode.Start.Line, (int)keyNode.Start.Column);
                var url = GetScalar(sourceMap, "url");
                var itemsP = GetScalar(sourceMap, "items_path");
                var valueP = GetScalar(sourceMap, "value_property");
                var labelP = GetScalar(sourceMap, "label_property");
                if (url is null || itemsP is null || valueP is null || labelP is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        DiagnosticCodes.ParameterSourceInvalid,
                        $"parameter '{name}' has a `source:` block missing required field(s)",
                        paramLoc,
                        "https://docs.sigil.build/diagnostics/SIG0234"));
                }
                else
                {
                    source = new ParameterSource(url, itemsP, valueP, labelP);
                }
            }

            dict[name] = new ParameterDefinition(
                Name: name,
                Type: type,
                Default: defaultValue,
                EnumValues: values,
                InstallTime: installTime,
                Description: description,
                Pattern: pattern,
                Min: min,
                Max: max,
                Source: source,
                Screen: screen);
        }
        return dict;
    }

    // Per-step "known field" allowlists. Hoisted to static readonly fields so the
    // analyzer (CA1861) doesn't fault us for allocating them on every step parse.
    private static readonly string[] FileCopyFields = { "id", "type", "when", "on_failure", "from", "to", "overwrite" };
    private static readonly string[] DirectoryCreateFields = { "id", "type", "when", "on_failure", "path" };
    private static readonly string[] FileDeleteFields = { "id", "type", "when", "on_failure", "path", "if_missing" };
    private static readonly string[] DirectoryDeleteFields = { "id", "type", "when", "on_failure", "path", "recursive" };
    private static readonly string[] RegistryWriteFields = { "id", "type", "when", "on_failure", "hive", "key", "name", "type_value", "value_type", "value", "view" };
    private static readonly string[] RegistryDeleteValueFields = { "id", "type", "when", "on_failure", "hive", "key", "name", "view" };
    private static readonly string[] RegistryDeleteKeyFields = { "id", "type", "when", "on_failure", "hive", "key", "recursive", "view" };
    private static readonly string[] ShortcutCreateFields = { "id", "type", "when", "on_failure", "target", "location", "name", "args", "working_dir", "icon", "description" };
    private static readonly string[] EnvSetFields = { "id", "type", "when", "on_failure", "name", "value", "scope", "action", "separator" };
    private static readonly string[] RunProgramFields = { "id", "type", "when", "on_failure", "program", "args", "wait", "cwd", "expected_exit_codes", "timeout_seconds" };
    private static readonly string[] HttpDownloadFields = { "id", "type", "when", "on_failure", "url", "dest", "sha256", "timeout_seconds", "retries" };
    private static readonly string[] IniWriteFields = { "id", "type", "when", "on_failure", "path", "section", "key", "value", "create_if_missing" };
    private static readonly string[] JsonEditFields = { "id", "type", "when", "on_failure", "path", "pointer", "value", "create_if_missing" };
    private static readonly string[] XmlEditFields = { "id", "type", "when", "on_failure", "path", "xpath", "attribute", "value", "create_if_missing" };

    private static List<InstallStep>? ParseInstallSteps(
        List<YamlMappingNode>? nodes, InstallScope scope, List<Diagnostic> diagnostics, string fileName,
        OnFailure defaultOnFailure = OnFailure.Fail)
    {
        if (nodes is null) return null;
        var list = new List<InstallStep>(nodes.Count);
        foreach (var node in nodes)
        {
            var step = ParseInstallStep(node, scope, diagnostics, fileName, defaultOnFailure);
            if (step is not null) list.Add(step);
        }
        return list;
    }

    private static InstallStep? ParseInstallStep(
        YamlMappingNode node, InstallScope scope, List<Diagnostic> diagnostics, string fileName,
        OnFailure defaultOnFailure = OnFailure.Fail)
    {
        var loc = new SourceLocation(fileName, (int)node.Start.Line, (int)node.Start.Column);
        var id = GetScalar(node, "id");
        var typeStr = GetScalar(node, "type");

        if (string.IsNullOrEmpty(id))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.MissingRequiredStepField,
                "install step is missing required field 'id'",
                loc,
                "https://docs.sigil.build/diagnostics/SIG0232"));
            return null;
        }

        if (string.IsNullOrEmpty(typeStr))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.MissingRequiredStepField,
                $"install step '{id}' is missing required field 'type'",
                loc,
                "https://docs.sigil.build/diagnostics/SIG0232"));
            return null;
        }

        var when = GetScalar(node, "when");
        // An absent on_failure uses the caller's phase default (Fail for the
        // journaled bodies; per-phase for P2 lifecycle hooks). An explicit value
        // is always honored.
        var onFailureRaw = GetScalar(node, "on_failure");
        var onFailure = onFailureRaw is null ? defaultOnFailure : ParseOnFailure(onFailureRaw);

        var step = typeStr switch
        {
            "file_copy" => BuildFileCopy(node, id!, when, onFailure, diagnostics, loc),
            "directory_create" => BuildDirectoryCreate(node, id!, when, onFailure, diagnostics, loc),
            "file_delete" => BuildFileDelete(node, id!, when, onFailure, diagnostics, loc),
            "directory_delete" => BuildDirectoryDelete(node, id!, when, onFailure, diagnostics, loc),
            "registry_write" => BuildRegistryWrite(node, id!, when, onFailure, diagnostics, loc),
            "registry_delete_value" => BuildRegistryDeleteValue(node, id!, when, onFailure, diagnostics, loc),
            "registry_delete_key" => BuildRegistryDeleteKey(node, id!, when, onFailure, diagnostics, loc),
            "shortcut_create" => BuildShortcutCreate(node, id!, when, onFailure, diagnostics, loc),
            "env_set" => BuildEnvSet(node, id!, when, onFailure, diagnostics, loc),
            "run_program" => BuildRunProgram(node, id!, when, onFailure, diagnostics, loc),
            "http_download" => BuildHttpDownload(node, id!, when, onFailure, diagnostics, loc),
            "ini_write" => BuildIniWrite(node, id!, when, onFailure, diagnostics, loc),
            "json_edit" => BuildJsonEdit(node, id!, when, onFailure, diagnostics, loc),
            "xml_edit" => BuildXmlEdit(node, id!, when, onFailure, diagnostics, loc),
            "service_install" => BuildServiceInstall(node, id!, when, onFailure, diagnostics, loc),
            "scheduled_task_create" => BuildScheduledTaskCreate(node, id!, when, onFailure, diagnostics, loc),
            "com_register" => BuildComRegister(node, id!, when, onFailure, diagnostics, loc),
            "firewall_rule" => BuildFirewallRule(node, id!, when, onFailure, diagnostics, loc),
            _ => ReportUnknownStepType(id!, typeStr!, loc, diagnostics),
        };

        // P11: guard machine-scope-only steps (SIG0310) right here, at the same
        // call site that already holds this step's own precise node `loc` — the
        // same location the SIG0230/SIG0231/SIG0232 diagnostics above use.
        if (step is not null)
        {
            MachineScopeGuard.ValidateStep(step, scope, loc, diagnostics);
        }

        return step;
    }

    private static readonly string[] ServiceInstallFields =
    {
        "id", "type", "when", "on_failure",
        "name", "binary_path", "display_name", "description",
        "start_type", "service_account", "start_after_install",
    };

    private static InstallStep.ServiceInstall? BuildServiceInstall(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var name = GetScalar(node, "name");
        var binaryPath = GetScalar(node, "binary_path");
        if (name is null) { ReportMissingField(id, "service_install", "name", loc, diagnostics); return null; }
        if (binaryPath is null) { ReportMissingField(id, "service_install", "binary_path", loc, diagnostics); return null; }

        var displayName = GetScalar(node, "display_name") ?? name;
        var description = GetScalar(node, "description");
        var startType = GetScalar(node, "start_type") ?? "auto";
        var serviceAccount = GetScalar(node, "service_account") ?? "LocalSystem";
        var startAfterInstall = GetBool(node, "start_after_install", defaultValue: true);

        ReportUnknownStepFields(node, id, "service_install", ServiceInstallFields, loc, diagnostics);
        return new InstallStep.ServiceInstall(
            id, name, binaryPath, displayName, description,
            startType, serviceAccount, startAfterInstall, when, onFailure);
    }

    private static readonly string[] ScheduledTaskCreateFields =
    {
        "id", "type", "when", "on_failure",
        "name", "program", "arguments", "trigger", "run_level",
    };

    private static readonly string[] ScheduledTaskTriggerValues = { "logon", "daily", "onstart" };
    private static readonly string[] ScheduledTaskRunLevelValues = { "limited", "highest" };

    private static InstallStep.ScheduledTaskCreate? BuildScheduledTaskCreate(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var name = GetScalar(node, "name");
        var program = GetScalar(node, "program");
        var trigger = GetScalar(node, "trigger");
        if (name is null) { ReportMissingField(id, "scheduled_task_create", "name", loc, diagnostics); return null; }
        if (program is null) { ReportMissingField(id, "scheduled_task_create", "program", loc, diagnostics); return null; }
        if (trigger is null) { ReportMissingField(id, "scheduled_task_create", "trigger", loc, diagnostics); return null; }

        if (!ScheduledTaskTriggerValues.Contains(trigger))
        {
            ReportBadEnumValue(id, "scheduled_task_create", "trigger", trigger, ScheduledTaskTriggerValues, loc, diagnostics);
            return null;
        }

        var arguments = GetScalar(node, "arguments");
        var runLevel = GetScalar(node, "run_level") ?? "limited";
        if (!ScheduledTaskRunLevelValues.Contains(runLevel))
        {
            ReportBadEnumValue(id, "scheduled_task_create", "run_level", runLevel, ScheduledTaskRunLevelValues, loc, diagnostics);
            return null;
        }

        ReportUnknownStepFields(node, id, "scheduled_task_create", ScheduledTaskCreateFields, loc, diagnostics);
        return new InstallStep.ScheduledTaskCreate(id, name, program, arguments, trigger, runLevel, when, onFailure);
    }

    private static readonly string[] ComRegisterFields =
    {
        "id", "type", "when", "on_failure", "path",
    };

    /// <summary>
    /// P11 (T11.2): <c>com_register</c> — self-registers a COM DLL via its
    /// exported <c>DllRegisterServer</c> at install time. Only <c>path</c> is
    /// required (missing → SIG0232); there are no enum-valued fields, so SIG0233
    /// does not apply here. The step is machine-scope-only (SIG0310), enforced by
    /// <see cref="InstallStep.RequiresMachineScope"/> on the typed record.
    /// </summary>
    private static InstallStep.ComRegister? BuildComRegister(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var path = GetScalar(node, "path");
        if (path is null) { ReportMissingField(id, "com_register", "path", loc, diagnostics); return null; }

        ReportUnknownStepFields(node, id, "com_register", ComRegisterFields, loc, diagnostics);
        return new InstallStep.ComRegister(id, path, when, onFailure);
    }

    private static readonly string[] FirewallRuleFields =
    {
        "id", "type", "when", "on_failure",
        "name", "direction", "action", "program", "port", "protocol",
    };

    private static readonly string[] FirewallDirectionValues = { "in", "out" };
    private static readonly string[] FirewallActionValues = { "allow", "block" };
    private static readonly string[] FirewallProtocolValues = { "tcp", "udp" };

    /// <summary>
    /// P11 (T11.3): <c>firewall_rule</c> — creates a Windows Defender Firewall
    /// rule via <c>netsh advfirewall firewall add rule</c>. <c>name</c>,
    /// <c>direction</c>, and <c>action</c> are required (missing → SIG0232);
    /// <c>direction</c>/<c>action</c>/<c>protocol</c> are enum-valued fields —
    /// a value outside their allowed set is SIG0233.
    /// </summary>
    /// <remarks>
    /// Port/protocol validation rule (documented per the brief, "keep it
    /// simple"): netsh's <c>localport=</c> needs an accompanying
    /// <c>protocol=</c>, so when <c>port</c> is given and <c>protocol</c> is
    /// absent, the parser defaults <c>protocol</c> to <c>tcp</c> rather than
    /// forcing every manifest author to spell out the common case. An
    /// explicitly-given <c>protocol</c> is still validated against the
    /// tcp/udp enum regardless of whether <c>port</c> is set.
    /// </remarks>
    private static InstallStep.FirewallRule? BuildFirewallRule(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var name = GetScalar(node, "name");
        var direction = GetScalar(node, "direction");
        var action = GetScalar(node, "action");
        if (name is null) { ReportMissingField(id, "firewall_rule", "name", loc, diagnostics); return null; }
        if (direction is null) { ReportMissingField(id, "firewall_rule", "direction", loc, diagnostics); return null; }
        if (action is null) { ReportMissingField(id, "firewall_rule", "action", loc, diagnostics); return null; }

        if (!FirewallDirectionValues.Contains(direction))
        {
            ReportBadEnumValue(id, "firewall_rule", "direction", direction, FirewallDirectionValues, loc, diagnostics);
            return null;
        }
        if (!FirewallActionValues.Contains(action))
        {
            ReportBadEnumValue(id, "firewall_rule", "action", action, FirewallActionValues, loc, diagnostics);
            return null;
        }

        var program = GetScalar(node, "program");
        var port = GetNullableInt(node, "port");
        var protocol = GetScalar(node, "protocol");
        if (protocol is not null && !FirewallProtocolValues.Contains(protocol))
        {
            ReportBadEnumValue(id, "firewall_rule", "protocol", protocol, FirewallProtocolValues, loc, diagnostics);
            return null;
        }

        // See the remarks on this method: default protocol=tcp when a port is
        // given but the author left protocol unset.
        if (port is not null && protocol is null)
        {
            protocol = "tcp";
        }

        ReportUnknownStepFields(node, id, "firewall_rule", FirewallRuleFields, loc, diagnostics);
        return new InstallStep.FirewallRule(id, name, direction, action, program, port, protocol, when, onFailure);
    }

    private static InstallStep? ReportUnknownStepType(
        string id, string typeStr, SourceLocation loc, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            DiagnosticCodes.UnknownStepType,
            $"unknown install step type '{typeStr}' for step '{id}'",
            loc,
            "https://docs.sigil.build/diagnostics/SIG0230"));
        return null;
    }

    private static OnFailure ParseOnFailure(string? raw) => raw switch
    {
        "rollback" => OnFailure.Rollback,
        "continue" => OnFailure.Continue,
        "fail" => OnFailure.Fail,
        null => OnFailure.Fail,
        _ => OnFailure.Fail,
    };

    private static InstallStep.FileCopy? BuildFileCopy(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var from = GetScalar(node, "from");
        var to = GetScalar(node, "to");
        if (from is null) { ReportMissingField(id, "file_copy", "from", loc, diagnostics); return null; }
        if (to is null) { ReportMissingField(id, "file_copy", "to", loc, diagnostics); return null; }
        var overwrite = GetBool(node, "overwrite", defaultValue: true);
        ReportUnknownStepFields(node, id, "file_copy", FileCopyFields, loc, diagnostics);
        return new InstallStep.FileCopy(id, from, to, overwrite, when, onFailure);
    }

    private static InstallStep.DirectoryCreate? BuildDirectoryCreate(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var path = GetScalar(node, "path");
        if (path is null) { ReportMissingField(id, "directory_create", "path", loc, diagnostics); return null; }
        ReportUnknownStepFields(node, id, "directory_create", DirectoryCreateFields, loc, diagnostics);
        return new InstallStep.DirectoryCreate(id, path, when, onFailure);
    }

    private static InstallStep.FileDelete? BuildFileDelete(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var path = GetScalar(node, "path");
        if (path is null) { ReportMissingField(id, "file_delete", "path", loc, diagnostics); return null; }
        var ifMissing = GetScalar(node, "if_missing") ?? "fail";
        ReportUnknownStepFields(node, id, "file_delete", FileDeleteFields, loc, diagnostics);
        return new InstallStep.FileDelete(id, path, ifMissing, when, onFailure);
    }

    private static InstallStep.DirectoryDelete? BuildDirectoryDelete(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var path = GetScalar(node, "path");
        if (path is null) { ReportMissingField(id, "directory_delete", "path", loc, diagnostics); return null; }
        var recursive = GetBool(node, "recursive", defaultValue: false);
        ReportUnknownStepFields(node, id, "directory_delete", DirectoryDeleteFields, loc, diagnostics);
        return new InstallStep.DirectoryDelete(id, path, recursive, when, onFailure);
    }

    private static InstallStep.RegistryWrite? BuildRegistryWrite(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var hive = GetScalar(node, "hive");
        var key = GetScalar(node, "key");
        var name = GetScalar(node, "name");
        var typeValue = GetScalar(node, "type_value") ?? GetScalar(node, "value_type");
        if (hive is null) { ReportMissingField(id, "registry_write", "hive", loc, diagnostics); return null; }
        if (key is null) { ReportMissingField(id, "registry_write", "key", loc, diagnostics); return null; }
        if (name is null) { ReportMissingField(id, "registry_write", "name", loc, diagnostics); return null; }
        // The step-level "type" field is reused for the step kind — registry value type
        // travels under the alias "type_value" (or "value_type"). If not provided, default to REG_SZ.
        var view = GetScalar(node, "view") ?? "native";
        var rawValue = GetRawValue(node, "value");
        ReportUnknownStepFields(node, id, "registry_write", RegistryWriteFields, loc, diagnostics);
        return new InstallStep.RegistryWrite(id, hive, key, name, typeValue ?? "REG_SZ", rawValue, view, when, onFailure);
    }

    private static InstallStep.RegistryDeleteValue? BuildRegistryDeleteValue(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var hive = GetScalar(node, "hive");
        var key = GetScalar(node, "key");
        var name = GetScalar(node, "name");
        if (hive is null) { ReportMissingField(id, "registry_delete_value", "hive", loc, diagnostics); return null; }
        if (key is null) { ReportMissingField(id, "registry_delete_value", "key", loc, diagnostics); return null; }
        if (name is null) { ReportMissingField(id, "registry_delete_value", "name", loc, diagnostics); return null; }
        var view = GetScalar(node, "view") ?? "native";
        ReportUnknownStepFields(node, id, "registry_delete_value", RegistryDeleteValueFields, loc, diagnostics);
        return new InstallStep.RegistryDeleteValue(id, hive, key, name, view, when, onFailure);
    }

    private static InstallStep.RegistryDeleteKey? BuildRegistryDeleteKey(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var hive = GetScalar(node, "hive");
        var key = GetScalar(node, "key");
        if (hive is null) { ReportMissingField(id, "registry_delete_key", "hive", loc, diagnostics); return null; }
        if (key is null) { ReportMissingField(id, "registry_delete_key", "key", loc, diagnostics); return null; }
        var recursive = GetBool(node, "recursive", defaultValue: false);
        var view = GetScalar(node, "view") ?? "native";
        ReportUnknownStepFields(node, id, "registry_delete_key", RegistryDeleteKeyFields, loc, diagnostics);
        return new InstallStep.RegistryDeleteKey(id, hive, key, recursive, view, when, onFailure);
    }

    private static InstallStep.ShortcutCreate? BuildShortcutCreate(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var target = GetScalar(node, "target");
        var location = GetScalar(node, "location");
        var name = GetScalar(node, "name");
        if (target is null) { ReportMissingField(id, "shortcut_create", "target", loc, diagnostics); return null; }
        if (location is null) { ReportMissingField(id, "shortcut_create", "location", loc, diagnostics); return null; }
        if (name is null) { ReportMissingField(id, "shortcut_create", "name", loc, diagnostics); return null; }
        var args = GetSequence(node, "args");
        var workingDir = GetScalar(node, "working_dir");
        var icon = GetScalar(node, "icon");
        var description = GetScalar(node, "description");
        ReportUnknownStepFields(node, id, "shortcut_create", ShortcutCreateFields, loc, diagnostics);
        return new InstallStep.ShortcutCreate(id, target, location, name, args, workingDir, icon, description, when, onFailure);
    }

    private static InstallStep.EnvSet? BuildEnvSet(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var name = GetScalar(node, "name");
        var value = GetScalar(node, "value");
        if (name is null) { ReportMissingField(id, "env_set", "name", loc, diagnostics); return null; }
        if (value is null) { ReportMissingField(id, "env_set", "value", loc, diagnostics); return null; }
        var scope = GetScalar(node, "scope") ?? "user";
        var action = GetScalar(node, "action") ?? "set";
        var separator = GetScalar(node, "separator") ?? ";";
        ReportUnknownStepFields(node, id, "env_set", EnvSetFields, loc, diagnostics);
        return new InstallStep.EnvSet(id, name, value, scope, action, separator, when, onFailure);
    }

    private static InstallStep.RunProgram? BuildRunProgram(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var program = GetScalar(node, "program");
        if (program is null) { ReportMissingField(id, "run_program", "program", loc, diagnostics); return null; }
        var args = GetSequence(node, "args");
        var wait = GetBool(node, "wait", defaultValue: true);
        var cwd = GetScalar(node, "cwd");
        var expectedExitCodes = GetIntSequence(node, "expected_exit_codes");
        var timeoutSeconds = GetNullableInt(node, "timeout_seconds");
        ReportUnknownStepFields(node, id, "run_program", RunProgramFields, loc, diagnostics);
        return new InstallStep.RunProgram(id, program, args, wait, cwd, expectedExitCodes, timeoutSeconds, when, onFailure);
    }

    private static InstallStep.HttpDownload? BuildHttpDownload(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var url = GetScalar(node, "url");
        var dest = GetScalar(node, "dest");
        var sha256 = GetScalar(node, "sha256");
        if (url is null) { ReportMissingField(id, "http_download", "url", loc, diagnostics); return null; }
        if (dest is null) { ReportMissingField(id, "http_download", "dest", loc, diagnostics); return null; }

        // sha256 is REQUIRED — refuse to pack a download without an integrity check.
        if (string.IsNullOrWhiteSpace(sha256))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.HttpDownloadChecksumRequired,
                $"http_download step '{id}' must declare a 'sha256' — a download without an integrity checksum is refused",
                loc,
                "https://docs.sigil.build/diagnostics/SIG0236"));
            return null;
        }

        // HTTPS only. A literal http:// URL is rejected at pack time; a URL built
        // from {var.*}/{install_dir} tokens is additionally re-checked at run time.
        if (url.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.HttpDownloadInsecureUrl,
                $"http_download step '{id}' url must be https:// (got '{url}')",
                loc,
                "https://docs.sigil.build/diagnostics/SIG0235"));
            return null;
        }

        var timeoutSeconds = GetNullableInt(node, "timeout_seconds");
        var retries = GetNullableInt(node, "retries");
        ReportUnknownStepFields(node, id, "http_download", HttpDownloadFields, loc, diagnostics);
        return new InstallStep.HttpDownload(id, url, dest, sha256, timeoutSeconds, retries, when, onFailure);
    }

    private static InstallStep.IniWrite? BuildIniWrite(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var path = GetScalar(node, "path");
        var key = GetScalar(node, "key");
        if (path is null) { ReportMissingField(id, "ini_write", "path", loc, diagnostics); return null; }
        if (key is null) { ReportMissingField(id, "ini_write", "key", loc, diagnostics); return null; }
        var section = GetScalar(node, "section") ?? string.Empty;
        var value = GetScalar(node, "value") ?? string.Empty;
        var createIfMissing = GetBool(node, "create_if_missing", defaultValue: false);
        ReportUnknownStepFields(node, id, "ini_write", IniWriteFields, loc, diagnostics);
        return new InstallStep.IniWrite(id, path, section, key, value, createIfMissing, when, onFailure);
    }

    private static InstallStep.JsonEdit? BuildJsonEdit(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var path = GetScalar(node, "path");
        var pointer = GetScalar(node, "pointer");
        if (path is null) { ReportMissingField(id, "json_edit", "path", loc, diagnostics); return null; }
        if (pointer is null) { ReportMissingField(id, "json_edit", "pointer", loc, diagnostics); return null; }
        var value = GetScalar(node, "value") ?? string.Empty;
        var createIfMissing = GetBool(node, "create_if_missing", defaultValue: false);
        ReportUnknownStepFields(node, id, "json_edit", JsonEditFields, loc, diagnostics);
        return new InstallStep.JsonEdit(id, path, pointer, value, createIfMissing, when, onFailure);
    }

    private static InstallStep.XmlEdit? BuildXmlEdit(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var path = GetScalar(node, "path");
        var xpath = GetScalar(node, "xpath");
        if (path is null) { ReportMissingField(id, "xml_edit", "path", loc, diagnostics); return null; }
        if (xpath is null) { ReportMissingField(id, "xml_edit", "xpath", loc, diagnostics); return null; }
        var attribute = GetScalar(node, "attribute");
        var value = GetScalar(node, "value") ?? string.Empty;
        var createIfMissing = GetBool(node, "create_if_missing", defaultValue: false);
        ReportUnknownStepFields(node, id, "xml_edit", XmlEditFields, loc, diagnostics);
        return new InstallStep.XmlEdit(id, path, xpath, attribute, value, createIfMissing, when, onFailure);
    }

    private static void ReportMissingField(
        string id, string stepType, string field, SourceLocation loc, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            DiagnosticCodes.MissingRequiredStepField,
            $"install step '{id}' (type {stepType}) is missing required field '{field}'",
            loc,
            "https://docs.sigil.build/diagnostics/SIG0232"));
    }

    /// <summary>
    /// P11: an enum-valued step field (e.g. <c>scheduled_task_create.trigger</c> /
    /// <c>run_level</c>) holds a value outside its allowed set. Unlike
    /// <see cref="ReportUnknownStepFields"/>'s unrecognized-key warning, a bad
    /// enum value makes the step's runtime behavior undefined (there is no safe
    /// fallback schtasks.exe mapping), so this is an Error, not a Warning — the
    /// step is refused (its Build… method returns null) rather than packed with
    /// a guessed default.
    /// </summary>
    private static void ReportBadEnumValue(
        string id, string stepType, string field, string value, string[] allowed,
        SourceLocation loc, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            DiagnosticCodes.InvalidStepFieldValue,
            $"install step '{id}' (type {stepType}) has invalid '{field}' value '{value}'; " +
            $"expected one of: {string.Join(", ", allowed)}",
            loc,
            "https://docs.sigil.build/diagnostics/SIG0233"));
    }

    private static void ReportUnknownStepFields(
        YamlMappingNode node, string id, string stepType,
        string[] knownFields, SourceLocation loc, List<Diagnostic> diagnostics)
    {
        foreach (var kvp in node.Children)
        {
            if (kvp.Key is not YamlScalarNode keyNode) continue;
            var key = keyNode.Value;
            if (key is null) continue;
            var known = false;
            foreach (var f in knownFields)
            {
                if (string.Equals(f, key, System.StringComparison.Ordinal)) { known = true; break; }
            }
            if (!known)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    DiagnosticCodes.StepParameterMismatch,
                    $"install step '{id}' (type {stepType}) has unknown field '{key}' — ignored",
                    loc,
                    "https://docs.sigil.build/diagnostics/SIG0231"));
            }
        }
    }

    private static bool TryParseParameterType(string? raw, out ParameterType type)
    {
        switch (raw)
        {
            case "string": type = ParameterType.String; return true;
            case "path": type = ParameterType.Path; return true;
            case "bool": type = ParameterType.Bool; return true;
            case "int": type = ParameterType.Int; return true;
            case "enum": type = ParameterType.Enum; return true;
            case "secret": type = ParameterType.Secret; return true;
            default: type = default; return false;
        }
    }

    private static object? ReadDefault(YamlMappingNode node, ParameterType type)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode("default"), out var raw)
            || raw is not YamlScalarNode scalar
            || scalar.Value is null)
        {
            return null;
        }

        return type switch
        {
            ParameterType.Bool => bool.TryParse(scalar.Value, out var b) ? b : (object?)scalar.Value,
            ParameterType.Int => int.TryParse(scalar.Value, out var i) ? i : (object?)scalar.Value,
            _ => scalar.Value,
        };
    }

    private static PackageFormat ParseFormat(string s) => s switch
    {
        "msix" => PackageFormat.Msix,
        "zip" => PackageFormat.Zip,
        "exe" => PackageFormat.Exe,
        _ => throw new YamlException($"unknown package format '{s}'"),
    };

    private static TargetArchitecture ParseArch(string s) => s switch
    {
        "x64" => TargetArchitecture.X64,
        "arm64" => TargetArchitecture.Arm64,
        _ => throw new YamlException($"unknown architecture '{s}'"),
    };

    private static YamlMappingNode? GetMapping(YamlMappingNode parent, string key, bool required = false)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlMappingNode m)
            return m;
        if (required) throw new YamlException($"required mapping '{key}' is missing");
        return null;
    }

    private static string? GetScalar(YamlMappingNode parent, string key)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode s)
            return s.Value;
        return null;
    }

    private static int GetInt(YamlMappingNode parent, string key, int defaultValue)
    {
        var s = GetScalar(parent, key);
        return s is not null && int.TryParse(s, out var n) ? n : defaultValue;
    }

    private static int? GetNullableInt(YamlMappingNode parent, string key)
    {
        var s = GetScalar(parent, key);
        return s is not null && int.TryParse(s, out var n) ? n : null;
    }

    private static bool GetBool(YamlMappingNode parent, string key, bool defaultValue)
    {
        var s = GetScalar(parent, key);
        return s is not null && bool.TryParse(s, out var b) ? b : defaultValue;
    }

    private static string[]? GetSequence(YamlMappingNode parent, string key)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlSequenceNode seq)
            return seq.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? "").ToArray();
        return null;
    }

    private static int[]? GetIntSequence(YamlMappingNode parent, string key)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlSequenceNode seq)
        {
            var list = new List<int>(seq.Children.Count);
            foreach (var child in seq.Children.OfType<YamlScalarNode>())
            {
                if (int.TryParse(child.Value, out var n)) list.Add(n);
            }
            return list.ToArray();
        }
        return null;
    }

    private static List<YamlMappingNode>? GetSequenceOfMappings(YamlMappingNode parent, string key)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlSequenceNode seq)
        {
            var list = new List<YamlMappingNode>(seq.Children.Count);
            foreach (var child in seq.Children)
            {
                if (child is YamlMappingNode m) list.Add(m);
            }
            return list;
        }
        return null;
    }

    private static object? GetRawValue(YamlMappingNode parent, string key)
    {
        if (!parent.Children.TryGetValue(new YamlScalarNode(key), out var node)) return null;
        return node switch
        {
            YamlScalarNode s => s.Value,
            YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(c => c.Value ?? "").ToArray(),
            _ => null,
        };
    }
}
