// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Core.Diagnostics;

/// <summary>
/// Every diagnostic code Sigil can emit. This table is the only place a code may be
/// spelled (R82).
/// </summary>
/// <remarks>
/// Codes used to be written as raw string literals at the call site, which is how
/// **four** numbers came to mean two unrelated things each — `SIG0210`, `SIG0220`,
/// `SIG0270` and `SIG0300` were each claimed by a manifest-validation error and,
/// separately, by a packaging or signing failure. A code's whole job is to identify
/// one failure well enough to document it at one URL, so a second meaning does not
/// make it ambiguous, it makes it useless. Two tests enforce the rule that fixed it:
/// one refuses a raw `SIG0xxx` literal at a `Diagnostic` construction site anywhere
/// in `src/`, the other asserts every value in this table is distinct.
/// </remarks>
public static class DiagnosticCodes
{
    public const string YamlSyntaxError = "SIG0001";
    public const string FileNotFound = "SIG0002";
    public const string SpecMismatch = "SIG0003";
    public const string SchemaViolation = "SIG0010";
    public const string EnvVariableMissing = "SIG0020";
    public const string MissingOptionalField = "SIG0050";

    // SIG01xx — packaging. The pack backends' own failures: a host that cannot
    // produce a format, a missing SDK, a tool that exited non-zero. Distinct from
    // the SIG02xx/SIG03xx manifest bands because nothing here is the manifest's
    // fault — the document is valid and the machine cannot honour it.
    public const string MsixRequiresWindows = "SIG0100";
    public const string WindowsSdkNotFound = "SIG0101";
    public const string MakeAppxFailed = "SIG0110";
    public const string WackNotInstalled = "SIG0111";
    public const string WackReportedFailures = "SIG0112";
    public const string WrapperRuntimeMissing = "SIG0120";

    // SIG0121 — `exe` requested on a non-Windows pack host. Emitted as a literal
    // "SIG0270" until R82, which is the collision that row was filed for: SIG0270
    // is installer.vars, so the diagnostics URL for a pack-host refusal pointed at
    // a manifest-validation page.
    public const string ExeFormatRequiresWindowsHost = "SIG0121";

    // SIG0122 — build.source names a directory that is not on disk. In the
    // packaging band rather than a manifest band on purpose: the document is
    // valid YAML against a valid schema, and only the filesystem can contradict
    // it. Refusing here is what stops a pack from succeeding with nothing to
    // pack (R7).
    public const string BuildSourceNotFound = "SIG0122";

    // SIG02xx — parameters: block
    public const string UnknownParameterType = "SIG0210";
    public const string ParameterValidationFailure = "SIG0220";

    // SIG023x — install_steps: block
    public const string UnknownStepType = "SIG0230";
    public const string StepParameterMismatch = "SIG0231";
    public const string MissingRequiredStepField = "SIG0232";

    // SIG0233 — a step field holds a value outside its allowed set (e.g. a bad
    // enum like scheduled_task_create's trigger/run_level). Fatal (Error): there
    // is no safe fallback mapping for an unrecognized enum value.
    public const string InvalidStepFieldValue = "SIG0233";

    // SIG0234 — parameter `source:` block validation
    public const string ParameterSourceInvalid = "SIG0234";

    // SIG0235/6 — http_download step validation. Both are fatal: the packer
    // refuses to emit a download that isn't HTTPS or lacks an integrity checksum.
    public const string HttpDownloadInsecureUrl = "SIG0235";
    public const string HttpDownloadChecksumRequired = "SIG0236";

    // SIG024x — installer.screens: block (declared custom screens)
    public const string UnknownScreenParameterRef = "SIG0240";
    public const string InvalidScreenWhenExpression = "SIG0241";
    public const string InvalidScreenTitleToken = "SIG0242";

    // SIG025x — installer.license: block (License screen backing)
    // Emitted at pack time when the referenced license file is
    // missing/unreadable/empty. Non-fatal: the pack succeeds and the License
    // screen is simply omitted.
    public const string LicenseFileUnreadable = "SIG0250";

    // SIG026x — installer.scope: block (dual install scope)
    // Emitted when installer.scope holds a value outside {user, machine, auto};
    // the parser falls back to auto. Non-fatal (the schema enum is the hard gate).
    public const string InvalidInstallerScope = "SIG0260";

    // SIG027x — installer.vars: block (declarative variables)
    // Emitted when a var expression is malformed or the vars form a reference
    // cycle. A cycle is fatal (Error) — there is no safe evaluation order.
    public const string InvalidInstallerVar = "SIG0270";

    // SIG028x — installer.prerequisites: block
    // Emitted (Error) when a prerequisite is missing name/detect/source, or an
    // https:// source omits the required sha256 integrity checksum.
    public const string InvalidPrerequisite = "SIG0280";

    // SIG029x — localization
    // SIG0290 is FATAL: every runtime fallback bottoms out at `en`, so a map
    // without it has no defined rendering. Pack diagnostics reach manifest
    // authors, who do not build under this repo's TreatWarningsAsErrors — a
    // warning here would genuinely ship blank strings.
    public const string LocalizedTextMissingEnglish = "SIG0290";
    public const string InvalidLanguageTag = "SIG0291";

    // SIG0300 — installer.options.components: block (custom components)
    // Emitted (Error) when a custom component's name is not a bare identifier,
    // collides with a built-in component or a declared parameter, duplicates
    // another custom component, or the component omits its required label.
    public const string InvalidCustomComponent = "SIG0300";

    // SIG031x — machine-scope-only install steps. Three steps
    // (scheduled_task_create, com_register, firewall_rule) touch machine-global
    // state; each overrides InstallStep.RequiresMachineScope to true.
    // SIG0310 is FATAL: it fires when such a step appears anywhere in the
    // manifest (install_steps/pre_install/post_install/uninstall or any
    // installer.hooks phase) while installer.scope is not `machine` — `auto`
    // resolves to per-user scope by default, so it fails the guard too.
    public const string SystemStepRequiresMachineScope = "SIG0310";

    // SIG0292 — a LocalizedText map's per-language value is not a plain scalar
    // (e.g. a nested sequence/mapping under a language key). Fatal for the same
    // reason as SIG0290: the value silently collapses to "" otherwise, which is
    // the same silent-blank-rendering failure shape one language key at a time.
    public const string LocalizedTextValueNotScalar = "SIG0292";

    // SIG032x — update engine channel manifest. Unlike the bands above,
    // these fire at UPDATE RUNTIME (inside the AOT wrapper/host, `/Update` mode),
    // not at pack time — there is no pack-time diagnostics list to append to, so
    // the runtime call path returns a typed parse/verify result carrying one of
    // these codes for the caller to log + map to a process exit code. The codes
    // stay the shared identifiers across both worlds (docs, logs, tests).
    //
    // SIG0320: the fetched channel manifest JSON fails to
    // parse, is missing a required field (version/packageUrl/sha256), declares
    // a non-https packageUrl, or declares an unsupported schemaVersion.
    public const string MalformedChannelManifest = "SIG0320";

    // SIG0321: the channel manifest's detached ECDSA P-256
    // signature (fetched from `manifestUrl + ".sig"`) fails to verify against
    // `updates.signingKey`.
    public const string ChannelManifestSignatureInvalid = "SIG0321";

    // SIG0322: the web-installer's package URL could not be resolved.
    // Emitted at PACK TIME by `sigil pack --payload web` when `--package-url` is
    // missing, empty, or not https:// — pack refuses rather than stamping a stub
    // whose synthesized http_download step could never succeed. Also reserved
    // for the analogous install-time bootstrap failure (the stub could not
    // resolve/download the package at that URL).
    public const string WebInstallerPackageUrlUnresolved = "SIG0322";

    // SIG0323-SIG0326 — network trust on manifest-declared URLs, keys, and the
    // downloaded-binary policy (R8, R14, R30, R45). Unlike
    // SIG0320-SIG0322 above, these fire at PACK TIME, in `ManifestParser`,
    // against the app manifest. They are one band because they share a subject
    // — what the update/parameter machinery may talk to and what it trusts —
    // not because they share a call path.

    // SIG0323: a `parameters.*.source.url` is not https://. The fetched values
    // become parameter values, which are substituted into step fields (paths,
    // registry coordinates, arguments) that execute elevated, so a cleartext
    // origin is an injection point into a privileged run. Mirrors SIG0235's
    // http_download stance; re-checked at install time in
    // `HttpOptionsLoader.LoadAsync`, because a URL built from tokens is not
    // knowable at pack time. (R8)
    public const string ParameterSourceInsecure = "SIG0323";

    // SIG0324: `updates.manifestUrl` is not https://. The schema constrains only
    // `format: uri`, so the scheme is gated here. Code execution is still gated
    // by the channel-manifest signature, so the impact is cleartext leakage of
    // app-id/version/channel plus a reliable update-suppression DoS. Re-checked
    // before the fetch at update runtime. (R14)
    public const string UpdateManifestUrlInsecure = "SIG0324";

    // SIG0325: `updates.signingKey` is not a base64-encoded X.509 SPKI DER of an
    // ECDSA P-256 PUBLIC key. Unvalidated, a private-key FILE PATH packs cleanly
    // and produces an installer whose every update attempt dies at SIG0321 —
    // failing closed, but only after shipping. (R30)
    public const string UpdateSigningKeyInvalid = "SIG0325";

    // SIG0326: `installer.require_signed_downloads` is not one of the declared
    // policy values. The policy governs whether a binary this run pulled off the
    // network must be Authenticode-valid before it is launched elevated, so an
    // unrecognized value is refused rather than silently falling back to the
    // default. (R45)
    public const string RequireSignedDownloadsInvalid = "SIG0326";

    // SIG04xx — signing. Its own band because signing fails for reasons that have
    // nothing to do with the manifest: a host without signtool, a certificate that
    // will not validate, a remote job that came back rejected.
    //
    // These moved here in R82. Local signing emitted SIG0200/SIG0210/SIG0220 and
    // Azure emitted SIG0300/SIG0301 as raw literals, and three of those numbers
    // were already taken by the parameters and custom-components bands — so
    // `sigil validate` and `sigil sign` could print the same code for unrelated
    // failures. Renumbering is safe precisely because no release has ever shipped:
    // no user, log or support page carries the old numbers.
    public const string LocalSigningRequiresWindows = "SIG0400";
    public const string SigningCertificateInvalid = "SIG0401";
    public const string SigntoolFailed = "SIG0402";
    public const string AzureSigningJobFailed = "SIG0410";
    public const string AzureSigningFailed = "SIG0411";

    /// <summary>
    /// The documentation URL for <paramref name="code"/>.
    /// </summary>
    /// <remarks>
    /// This is the ONLY place the documentation host is spelled. Call sites used to
    /// write the whole URL out, which put a second copy of the code in every
    /// diagnostic and let the two drift — renumbering the signing band in R82
    /// silently pointed five URLs at the old numbers until they were caught. All 40
    /// such literals now route through here, and a test forbids the host appearing
    /// anywhere else under <c>src/</c>.
    /// <para>
    /// The target is one page with a per-code anchor, not a page per code:
    /// <c>docs/diagnostics.md</c> renders at <c>/diagnostics/</c> and each entry
    /// carries an explicit lowercase <c>{#sigXXXX}</c> id. Lower-casing here is what
    /// matches those ids — a fragment is case-sensitive, and the codes are written
    /// upper-case everywhere else.
    /// </para>
    /// </remarks>
    public static string DocsUrl(string code) =>
        $"https://docs.sigil.build/diagnostics/#{code.ToLowerInvariant()}";
}
