namespace SigilBuild.Wrapper.Cli;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SigilBuild.Core.Manifest;

/// <summary>
/// Thrown when the install-time CLI receives unrecognized flags, undeclared
/// parameters, or otherwise malformed argument tokens. The message intentionally
/// names the offending token so end-users can correct typos without consulting
/// docs.
/// </summary>
public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }

    public UsageException() { }

    public UsageException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Operating mode for the wrapper, derived from the CLI flags.
/// </summary>
public enum WrapperMode
{
    /// <summary>Run the manifest's <c>install_steps</c> (default).</summary>
    Install,

    /// <summary>Run the manifest's <c>update_steps</c>; entered via <c>/Update</c> by the Update SDK.</summary>
    Update,

    /// <summary>Run the auto-derived uninstall sequence; entered via <c>/Uninstall</c>.</summary>
    Uninstall,
}

/// <summary>
/// Install-scope override requested on the command line. The concrete
/// scope/elevation behaviour is Task T12 — T2 only parses and stores the flag.
/// </summary>
public enum ScopeOverride
{
    /// <summary>No <c>/allusers</c> or <c>/currentuser</c> flag was supplied.</summary>
    None,

    /// <summary><c>/allusers</c> — machine (per-machine) install requested.</summary>
    AllUsers,

    /// <summary><c>/currentuser</c> — per-user install requested.</summary>
    CurrentUser,
}

/// <summary>
/// Result of parsing the wrapper's install-time argv. Captures the operating
/// mode, the silent-mode toggles, the resolved parameter override values
/// keyed by their canonical schema-defined name, plus the parse-and-store-only
/// install-dir / scope overrides.
/// </summary>
public sealed class ParsedCommandLine
{
    /// <summary>Operating mode requested by the user.</summary>
    public WrapperMode Mode { get; init; } = WrapperMode.Install;

    /// <summary>True if <c>/silent</c>, <c>/S</c>, or <c>/verysilent</c> was present — suppresses interactive UI.</summary>
    public bool Silent { get; init; }

    /// <summary>True if <c>/verysilent</c> was present — implies <see cref="Silent"/> plus suppressed progress UI.</summary>
    public bool VerySilent { get; init; }

    /// <summary>
    /// Parameter overrides: schema canonical name → value. Lookups are
    /// case-insensitive but the keys themselves preserve schema casing so
    /// downstream consumers don't have to know about the input spelling.
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Built-in-option overrides (<c>desktop_shortcut</c>, <c>add_to_path</c>, …):
    /// canonical option name → raw value. The option model itself lands in
    /// Task T8; T2 parses and stores the values so <c>/Pdesktop_shortcut=false</c>
    /// is accepted rather than rejected as an unknown token.
    /// </summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Install-dir override from <c>/D=path</c>; <c>null</c> if not supplied. Stored only (Task T13).</summary>
    public string? InstallDir { get; init; }

    /// <summary>Scope override from <c>/allusers</c> / <c>/currentuser</c>. Stored only (Task T12).</summary>
    public ScopeOverride Scope { get; init; } = ScopeOverride.None;

    /// <summary>
    /// True when <c>/force-downgrade</c> was supplied (P3): install an older version
    /// over an installed newer one instead of blocking. Ignored for fresh / same /
    /// upgrade runs.
    /// </summary>
    public bool ForceDowngrade { get; init; }

    /// <summary>Names (canonical casing) of the parameters whose schema type is <see cref="ParameterType.Secret"/>.</summary>
    public IReadOnlyList<string> SecretKeys { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when <c>/LOG</c> or <c>/LOG=path</c> was supplied — the run writes a
    /// timestamped install log (P7). Applies to install, uninstall, and update
    /// modes alike (the ARP <c>UninstallString</c> can carry <c>/LOG</c> too).
    /// </summary>
    public bool LogRequested { get; init; }

    /// <summary>
    /// Explicit log path from <c>/LOG=path</c>. <c>null</c> for bare <c>/LOG</c>
    /// (the session then defaults to <c>%TEMP%\sigil-&lt;appid&gt;.log</c>) or when
    /// logging was not requested.
    /// </summary>
    public string? LogPath { get; init; }

    /// <summary>
    /// True when <c>/launch</c> was supplied (P2, gap G4). A headless
    /// (<c>/silent</c>) install starts the <c>run_after_install</c> target only when
    /// this is set; the interactive wizard uses the Done-screen checkbox instead.
    /// </summary>
    public bool Launch { get; init; }

    /// <summary>
    /// True when <c>/closeapps</c> was supplied (P6, gap G7). A headless run whose
    /// install directory is held open by running applications closes them via the
    /// Restart Manager instead of refusing; without it the run exits with
    /// <c>InstallSession.FilesInUseExitCode</c>. The wizard uses the "Close
    /// applications" screen instead.
    /// </summary>
    public bool CloseApps { get; init; }

    /// <summary>
    /// Requested wizard language from /lang=&lt;tag&gt;. A fixed installer.language
    /// overrides this (design §2.1) — language is a display preference, so a
    /// conflict is logged and ignored rather than being a usage error like
    /// T12's fixed-scope vs /allusers.
    /// </summary>
    public string? Lang { get; init; }

    /// <summary>
    /// Re-renders the parsed args as a single space-joined string, with secret
    /// parameter values replaced by <c>***</c>. Suitable for logging without
    /// leaking license keys / passwords / tokens.
    /// </summary>
    public string AuditSafeRendering()
    {
        var secretSet = new HashSet<string>(SecretKeys, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        void Space()
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }
        }

        if (VerySilent)
        {
            sb.Append("/verysilent");
        }
        else if (Silent)
        {
            sb.Append("/silent");
        }

        switch (Mode)
        {
            case WrapperMode.Update:
                Space();
                sb.Append("/Update");
                break;
            case WrapperMode.Uninstall:
                Space();
                sb.Append("/Uninstall");
                break;
            case WrapperMode.Install:
            default:
                break;
        }

        switch (Scope)
        {
            case ScopeOverride.AllUsers:
                Space();
                sb.Append("/allusers");
                break;
            case ScopeOverride.CurrentUser:
                Space();
                sb.Append("/currentuser");
                break;
            case ScopeOverride.None:
            default:
                break;
        }

        if (ForceDowngrade)
        {
            Space();
            sb.Append("/force-downgrade");
        }

        if (InstallDir is not null)
        {
            Space();
            sb.Append("/D=");
            sb.Append(InstallDir);
        }

        if (LogRequested)
        {
            Space();
            sb.Append("/LOG");
            if (LogPath is not null)
            {
                sb.Append('=');
                sb.Append(LogPath);
            }
        }

        if (Launch)
        {
            Space();
            sb.Append("/launch");
        }

        if (CloseApps)
        {
            Space();
            sb.Append("/closeapps");
        }

        if (Lang is not null)
        {
            Space();
            sb.Append("/lang=").Append(Lang);
        }

        foreach (var kv in Values)
        {
            Space();
            sb.Append("/P");
            sb.Append(kv.Key);
            sb.Append('=');
            sb.Append(secretSet.Contains(kv.Key) ? "***" : kv.Value);
        }

        foreach (var kv in Options)
        {
            Space();
            sb.Append("/P");
            sb.Append(kv.Key);
            sb.Append('=');
            sb.Append(kv.Value);
        }

        return sb.ToString();
    }
}

/// <summary>
/// Closed-grammar parser for the wrapper's install-time CLI. This is the single
/// parser shared by both entry points (the console <c>SigilBuild.Wrapper</c> and
/// the Avalonia <c>SigilBuild.Installer.Host</c>). Recognized tokens (Windows
/// installer convention):
/// <list type="bullet">
///   <item><description><c>/silent</c> (alias <c>/S</c>) — headless install. Implies
///   acceptance of the license (T14): the headless path never shows the License
///   screen, so a silent install proceeds without an interactive accept gate.</description></item>
///   <item><description><c>/verysilent</c> — headless install with suppressed progress.</description></item>
///   <item><description><c>/Update</c> — run <c>update_steps</c> instead of <c>install_steps</c>.</description></item>
///   <item><description><c>/Uninstall</c> — run the auto-derived uninstall sequence.</description></item>
///   <item><description><c>/allusers</c> / <c>/currentuser</c> — scope override (stored only; Task T12).</description></item>
///   <item><description><c>/force-downgrade</c> — install an older version over an installed newer one (P3).</description></item>
///   <item><description><c>/D=path</c> — install-dir override (stored only; Task T13).</description></item>
///   <item><description><c>/LOG</c> — write a timestamped install log to
///   <c>%TEMP%\sigil-&lt;appid&gt;.log</c>; <c>/LOG=path</c> — write it to <c>path</c> (P7).</description></item>
///   <item><description><c>/closeapps</c> — when the install directory is held open
///   by running applications, close them via the Restart Manager instead of refusing
///   the silent run (P6).</description></item>
///   <item><description><c>/launch</c> — after a silent install, start the
///   <c>run_after_install</c> target unelevated (P2). Ignored without <c>/silent</c>
///   (the wizard uses the Done-screen checkbox).</description></item>
///   <item><description><c>/P&lt;Name&gt;=&lt;Value&gt;</c> — override a declared parameter or a built-in option.</description></item>
/// </list>
/// Anything else is a <see cref="UsageException"/> — the parser is intentionally
/// closed so silent typos never reach the step engine.
/// </summary>
public static class CommandLineParser
{
    /// <summary>
    /// Built-in installer options (Task T8). T2 accepts and stores overrides for
    /// them so <c>/Pdesktop_shortcut=false</c> parses rather than exit-64s; the
    /// option model and generated steps land in T8.
    /// </summary>
    private static readonly HashSet<string> KnownOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop_shortcut",
        "start_menu",
        "add_to_path",
        "file_associations",
    };

    /// <summary>
    /// Parse <paramref name="args"/> against the parameter <paramref name="schema"/>.
    /// </summary>
    /// <exception cref="UsageException">
    /// Unknown flag, undeclared parameter/option, malformed token, or — in silent
    /// install mode — a required parameter (no default) left unset.
    /// </exception>
    /// <exception cref="ArgumentException">The schema itself is malformed (duplicate names).</exception>
    public static ParsedCommandLine Parse(IReadOnlyList<string> args, IReadOnlyList<ParameterDefinition> schema)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(schema);

        // Build a case-insensitive lookup over the schema, validating uniqueness.
        var byName = new Dictionary<string, ParameterDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in schema)
        {
            if (!byName.TryAdd(def.Name, def))
            {
                throw new ArgumentException(
                    $"duplicate parameter name '{def.Name}' in schema (case-insensitive)",
                    nameof(schema));
            }
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mode = WrapperMode.Install;
        var silent = false;
        var verySilent = false;
        var scope = ScopeOverride.None;
        var forceDowngrade = false;
        string? installDir = null;
        var logRequested = false;
        string? logPath = null;
        var launch = false;
        var closeApps = false;
        string? lang = null;

        foreach (var rawArg in args)
        {
            if (string.IsNullOrEmpty(rawArg))
            {
                throw new UsageException("empty argument is not allowed");
            }

            if (rawArg[0] != '/')
            {
                throw new UsageException(
                    $"unexpected positional argument '{rawArg}': only /silent, /S, /verysilent, /Update, /Uninstall, /allusers, /currentuser, /force-downgrade, /closeapps, /D=path, /LOG[=path], /lang=tag, /launch, and /PName=Value are accepted");
            }

            // Strip the leading '/'.
            var body = rawArg.Substring(1);

            // Bare flags first (case-insensitive, matches Windows installer convention).
            if (string.Equals(body, "silent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(body, "S", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
                continue;
            }
            if (string.Equals(body, "verysilent", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
                verySilent = true;
                continue;
            }
            if (string.Equals(body, "Update", StringComparison.OrdinalIgnoreCase))
            {
                mode = WrapperMode.Update;
                continue;
            }
            if (string.Equals(body, "Uninstall", StringComparison.OrdinalIgnoreCase))
            {
                mode = WrapperMode.Uninstall;
                continue;
            }
            if (string.Equals(body, "allusers", StringComparison.OrdinalIgnoreCase))
            {
                scope = ScopeOverride.AllUsers;
                continue;
            }
            if (string.Equals(body, "currentuser", StringComparison.OrdinalIgnoreCase))
            {
                scope = ScopeOverride.CurrentUser;
                continue;
            }
            if (string.Equals(body, "launch", StringComparison.OrdinalIgnoreCase))
            {
                launch = true;
                continue;
            }
            if (string.Equals(body, "closeapps", StringComparison.OrdinalIgnoreCase))
            {
                closeApps = true;
                continue;
            }
            if (string.Equals(body, "force-downgrade", StringComparison.OrdinalIgnoreCase))
            {
                forceDowngrade = true;
                continue;
            }

            // /D=path — install-dir override (parse + store only).
            if (body.Length >= 2 &&
                (body[0] == 'D' || body[0] == 'd') &&
                body[1] == '=')
            {
                var dir = body.Substring(2);
                if (dir.Length == 0)
                {
                    throw new UsageException($"'/D=' requires a path (offending token: '{rawArg}')");
                }
                installDir = dir;
                continue;
            }

            // /LOG or /LOG=path — install log (P7). Bare /LOG defaults the path to
            // %TEMP%\sigil-<appid>.log (resolved by the session, which knows the AppId).
            if (body.Length >= 3 &&
                (body[0] == 'L' || body[0] == 'l') &&
                (body[1] == 'O' || body[1] == 'o') &&
                (body[2] == 'G' || body[2] == 'g'))
            {
                if (body.Length == 3)
                {
                    logRequested = true;
                    continue;
                }
                if (body[3] == '=')
                {
                    var p = body.Substring(4);
                    if (p.Length == 0)
                    {
                        throw new UsageException($"'/LOG=' requires a path (offending token: '{rawArg}')");
                    }
                    logRequested = true;
                    logPath = p;
                    continue;
                }
                // Anything else (e.g. /LOGGING) is not /LOG — fall through to the
                // unknown-flag error so the closed grammar stays closed.
            }

            // /lang=<tag> — prefix form, like /D=. No collision: /launch is matched by
            // string.Equals above, and the /LOG branch tests body[1] == 'O'/'o'.
            if (body.Length >= 4
                && (body[0] is 'l' or 'L')
                && (body[1] is 'a' or 'A')
                && (body[2] is 'n' or 'N')
                && (body[3] is 'g' or 'G'))
            {
                if (body.Length < 6 || body[4] != '=')
                {
                    throw new UsageException(
                        $"'/lang=' requires a language tag (offending token: '{rawArg}')");
                }

                var tag = body.Substring(5);
                if (!LanguageTag.IsValid(tag))
                {
                    throw new UsageException(
                        $"'{tag}' is not a valid language tag (offending token: '{rawArg}'). " +
                        "Expected a tag like en, uk, or pt-BR.");
                }

                lang = tag;
                continue;
            }

            // /PName=Value — declared parameter or built-in option override.
            if (body.Length >= 1 && (body[0] == 'P' || body[0] == 'p'))
            {
                ParsePValue(rawArg, body.Substring(1), byName, values, options);
                continue;
            }

            throw new UsageException(
                $"unrecognized flag '{rawArg}': expected /silent, /S, /verysilent, /Update, /Uninstall, /allusers, /currentuser, /force-downgrade, /closeapps, /D=path, /LOG[=path], /lang=tag, /launch, or /PName=Value");
        }

        var secretKeys = schema
            .Where(p => p.Type == ParameterType.Secret)
            .Select(p => p.Name)
            .ToArray();

        // Silent install must be fully specified up-front: a required parameter
        // (declared, install-time, no default) that was not supplied cannot be
        // collected from the wizard, so fail loudly and name it. The interactive
        // path skips this — the wizard collects the values (Task T9).
        if (silent && mode == WrapperMode.Install)
        {
            var missing = schema
                .Where(p => p.InstallTime && p.Default is null && !values.ContainsKey(p.Name))
                .Select(p => p.Name)
                .ToArray();
            if (missing.Length > 0)
            {
                throw new UsageException(
                    $"silent install is missing required parameter(s) with no default: {string.Join(", ", missing)} (supply via /P{missing[0]}=value)");
            }
        }

        return new ParsedCommandLine
        {
            Mode = mode,
            Silent = silent,
            VerySilent = verySilent,
            Values = values,
            Options = options,
            InstallDir = installDir,
            Scope = scope,
            ForceDowngrade = forceDowngrade,
            SecretKeys = secretKeys,
            LogRequested = logRequested,
            LogPath = logPath,
            Launch = launch,
            CloseApps = closeApps,
            Lang = lang,
        };
    }

    private static void ParsePValue(
        string rawArg,
        string nameEqValue,
        Dictionary<string, ParameterDefinition> byName,
        Dictionary<string, string> values,
        Dictionary<string, string> options)
    {
        var eq = nameEqValue.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            throw new UsageException(
                $"malformed parameter token '{rawArg}': expected /PName=Value");
        }

        var inputName = nameEqValue.Substring(0, eq);
        var value = nameEqValue.Substring(eq + 1);

        if (byName.TryGetValue(inputName, out var def))
        {
            // Last-wins for duplicates. Preserve schema-canonical casing as the key.
            values[def.Name] = value;
            return;
        }

        if (KnownOptions.Contains(inputName))
        {
            options[inputName] = value;
            return;
        }

        throw new UsageException(
            $"'{inputName}' is neither a declared parameter nor a built-in option (offending token: '{rawArg}')");
    }
}
