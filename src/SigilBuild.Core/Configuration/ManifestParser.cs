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
        return new SigilManifest(
            Spec: GetScalar(root, "spec") ?? "",
            App: app,
            Build: MapBuild(GetMapping(root, "build", required: true)!),
            Package: MapPackage(GetMapping(root, "package")),
            Sign: MapSign(GetMapping(root, "sign")),
            Publish: MapPublish(GetMapping(root, "publish")),
            Updates: MapUpdates(GetMapping(root, "updates")),
            Installer: MapInstaller(GetMapping(root, "installer"), app, parameters, diagnostics, file),
            Location: loc,
            Parameters: parameters,
            InstallSteps: ParseInstallSteps(GetSequenceOfMappings(root, "install_steps"), diagnostics, file),
            PreInstall: ParseInstallSteps(GetSequenceOfMappings(root, "pre_install"), diagnostics, file),
            PostInstall: ParseInstallSteps(GetSequenceOfMappings(root, "post_install"), diagnostics, file),
            Uninstall: ParseInstallSteps(GetSequenceOfMappings(root, "uninstall"), diagnostics, file));
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
        var brand = GetMapping(node, "brand");
        var screens = ParseScreens(
            GetSequenceOfMappings(node, "screens"), app, parameters, diagnostics, fileName);
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
            Options: ParseOptions(GetMapping(node, "options")),
            Screens: screens,
            // T14: capture the license path string only. The actual file read +
            // embed happens at PACK time (ExeWrapperPackager.BuildBlobBytes), which
            // can resolve it against the pack source dir and emit a diagnostic if
            // the file is missing/unreadable/empty.
            License: GetScalar(node, "license"),
            // T12: install scope (user | machine | auto, default auto). The schema
            // enum is the hard gate; here we map the string leniently and emit a
            // non-fatal diagnostic on an unrecognized value, falling back to auto.
            Scope: ParseScope(node, diagnostics, fileName),
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
            Vars: ParseVars(GetMapping(node, "vars"), diagnostics, fileName));
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
    private static InstallerOptions? ParseOptions(YamlMappingNode? node)
    {
        if (node is null) return null;

        var desktop = ParseBoolOption(node, "desktop_shortcut");
        var startMenu = ParseBoolOption(node, "start_menu");
        var addToPath = ParseBoolOption(node, "add_to_path");
        var fileAssoc = ParseFileAssociationOption(node, "file_associations");

        if (desktop is null && startMenu is null && addToPath is null && fileAssoc is null)
        {
            return null;
        }

        return new InstallerOptions(desktop, startMenu, addToPath, fileAssoc);
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
            var title = GetScalar(node, "title") ?? string.Empty;
            var subtitle = GetScalar(node, "subtitle");
            var when = GetScalar(node, "when");

            ValidateInterpolationTokens(title, loc, diagnostics);
            if (subtitle is not null)
            {
                ValidateInterpolationTokens(subtitle, loc, diagnostics);
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
            var description = GetScalar(value, "description");
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
                var url     = GetScalar(sourceMap, "url");
                var itemsP  = GetScalar(sourceMap, "items_path");
                var valueP  = GetScalar(sourceMap, "value_property");
                var labelP  = GetScalar(sourceMap, "label_property");
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
    private static readonly string[] FileCopyFields            = { "id", "type", "when", "on_failure", "from", "to", "overwrite" };
    private static readonly string[] DirectoryCreateFields     = { "id", "type", "when", "on_failure", "path" };
    private static readonly string[] FileDeleteFields          = { "id", "type", "when", "on_failure", "path", "if_missing" };
    private static readonly string[] DirectoryDeleteFields     = { "id", "type", "when", "on_failure", "path", "recursive" };
    private static readonly string[] RegistryWriteFields       = { "id", "type", "when", "on_failure", "hive", "key", "name", "type_value", "value_type", "value", "view" };
    private static readonly string[] RegistryDeleteValueFields = { "id", "type", "when", "on_failure", "hive", "key", "name", "view" };
    private static readonly string[] RegistryDeleteKeyFields   = { "id", "type", "when", "on_failure", "hive", "key", "recursive", "view" };
    private static readonly string[] ShortcutCreateFields      = { "id", "type", "when", "on_failure", "target", "location", "name", "args", "working_dir", "icon", "description" };
    private static readonly string[] EnvSetFields              = { "id", "type", "when", "on_failure", "name", "value", "scope", "action", "separator" };
    private static readonly string[] RunProgramFields          = { "id", "type", "when", "on_failure", "program", "args", "wait", "cwd", "expected_exit_codes", "timeout_seconds" };

    private static List<InstallStep>? ParseInstallSteps(
        List<YamlMappingNode>? nodes, List<Diagnostic> diagnostics, string fileName)
    {
        if (nodes is null) return null;
        var list = new List<InstallStep>(nodes.Count);
        foreach (var node in nodes)
        {
            var step = ParseInstallStep(node, diagnostics, fileName);
            if (step is not null) list.Add(step);
        }
        return list;
    }

    private static InstallStep? ParseInstallStep(
        YamlMappingNode node, List<Diagnostic> diagnostics, string fileName)
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
        var onFailure = ParseOnFailure(GetScalar(node, "on_failure"));

        return typeStr switch
        {
            "file_copy"             => BuildFileCopy(node, id!, when, onFailure, diagnostics, loc),
            "directory_create"      => BuildDirectoryCreate(node, id!, when, onFailure, diagnostics, loc),
            "file_delete"           => BuildFileDelete(node, id!, when, onFailure, diagnostics, loc),
            "directory_delete"      => BuildDirectoryDelete(node, id!, when, onFailure, diagnostics, loc),
            "registry_write"        => BuildRegistryWrite(node, id!, when, onFailure, diagnostics, loc),
            "registry_delete_value" => BuildRegistryDeleteValue(node, id!, when, onFailure, diagnostics, loc),
            "registry_delete_key"   => BuildRegistryDeleteKey(node, id!, when, onFailure, diagnostics, loc),
            "shortcut_create"       => BuildShortcutCreate(node, id!, when, onFailure, diagnostics, loc),
            "env_set"               => BuildEnvSet(node, id!, when, onFailure, diagnostics, loc),
            "run_program"           => BuildRunProgram(node, id!, when, onFailure, diagnostics, loc),
            "service_install"       => BuildServiceInstall(node, id!, when, onFailure, diagnostics, loc),
            _ => ReportUnknownStepType(id!, typeStr!, loc, diagnostics),
        };
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
        if (name is null)        { ReportMissingField(id, "service_install", "name",        loc, diagnostics); return null; }
        if (binaryPath is null)  { ReportMissingField(id, "service_install", "binary_path", loc, diagnostics); return null; }

        var displayName       = GetScalar(node, "display_name") ?? name;
        var description       = GetScalar(node, "description");
        var startType         = GetScalar(node, "start_type") ?? "auto";
        var serviceAccount    = GetScalar(node, "service_account") ?? "LocalSystem";
        var startAfterInstall = GetBool(node, "start_after_install", defaultValue: true);

        ReportUnknownStepFields(node, id, "service_install", ServiceInstallFields, loc, diagnostics);
        return new InstallStep.ServiceInstall(
            id, name, binaryPath, displayName, description,
            startType, serviceAccount, startAfterInstall, when, onFailure);
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
        "fail"     => OnFailure.Fail,
        null       => OnFailure.Fail,
        _          => OnFailure.Fail,
    };

    private static InstallStep.FileCopy? BuildFileCopy(
        YamlMappingNode node, string id, string? when, OnFailure onFailure,
        List<Diagnostic> diagnostics, SourceLocation loc)
    {
        var from = GetScalar(node, "from");
        var to = GetScalar(node, "to");
        if (from is null) { ReportMissingField(id, "file_copy", "from", loc, diagnostics); return null; }
        if (to is null)   { ReportMissingField(id, "file_copy", "to",   loc, diagnostics); return null; }
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
        if (key  is null) { ReportMissingField(id, "registry_write", "key",  loc, diagnostics); return null; }
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
        if (key  is null) { ReportMissingField(id, "registry_delete_value", "key",  loc, diagnostics); return null; }
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
        if (key  is null) { ReportMissingField(id, "registry_delete_key", "key",  loc, diagnostics); return null; }
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
        if (target   is null) { ReportMissingField(id, "shortcut_create", "target",   loc, diagnostics); return null; }
        if (location is null) { ReportMissingField(id, "shortcut_create", "location", loc, diagnostics); return null; }
        if (name     is null) { ReportMissingField(id, "shortcut_create", "name",     loc, diagnostics); return null; }
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
        if (name  is null) { ReportMissingField(id, "env_set", "name",  loc, diagnostics); return null; }
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
            case "path":   type = ParameterType.Path;   return true;
            case "bool":   type = ParameterType.Bool;   return true;
            case "int":    type = ParameterType.Int;    return true;
            case "enum":   type = ParameterType.Enum;   return true;
            case "secret": type = ParameterType.Secret; return true;
            default:       type = default;              return false;
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
            ParameterType.Int  => int.TryParse(scalar.Value, out var i)  ? i : (object?)scalar.Value,
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
