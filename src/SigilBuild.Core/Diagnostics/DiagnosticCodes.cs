namespace SigilBuild.Core.Diagnostics;

public static class DiagnosticCodes
{
    public const string YamlSyntaxError = "SIG0001";
    public const string FileNotFound = "SIG0002";
    public const string SpecMismatch = "SIG0003";
    public const string SchemaViolation = "SIG0010";
    public const string EnvVariableMissing = "SIG0020";
    public const string MissingOptionalField = "SIG0050";

    // SIG02xx — parameters: block (Sprint 5c, Task 8)
    public const string UnknownParameterType = "SIG0210";
    public const string ParameterValidationFailure = "SIG0220";

    // SIG023x — install_steps: block (Sprint 5c, Task 9)
    public const string UnknownStepType = "SIG0230";
    public const string StepParameterMismatch = "SIG0231";
    public const string MissingRequiredStepField = "SIG0232";

    // SIG0233 — a step field holds a value outside its allowed set (e.g. a bad
    // enum like scheduled_task_create's trigger/run_level). Fatal (Error): there
    // is no safe fallback mapping for an unrecognized enum value.
    public const string InvalidStepFieldValue = "SIG0233";

    // SIG0234 — parameter `source:` block validation (uninstaller-icon-nsis-parity Section 8)
    public const string ParameterSourceInvalid = "SIG0234";

    // SIG0235/6 — http_download step validation (P4). Both are fatal: the packer
    // refuses to emit a download that isn't HTTPS or lacks an integrity checksum.
    public const string HttpDownloadInsecureUrl = "SIG0235";
    public const string HttpDownloadChecksumRequired = "SIG0236";

    // SIG024x — installer.screens: block (T9, declared custom screens)
    public const string UnknownScreenParameterRef = "SIG0240";
    public const string InvalidScreenWhenExpression = "SIG0241";
    public const string InvalidScreenTitleToken = "SIG0242";

    // SIG025x — installer.license: block (T14, License screen backing)
    // Emitted at pack time when the referenced license file is
    // missing/unreadable/empty. Non-fatal: the pack succeeds and the License
    // screen is simply omitted.
    public const string LicenseFileUnreadable = "SIG0250";

    // SIG026x — installer.scope: block (T12, dual install scope)
    // Emitted when installer.scope holds a value outside {user, machine, auto};
    // the parser falls back to auto. Non-fatal (the schema enum is the hard gate).
    public const string InvalidInstallerScope = "SIG0260";

    // SIG027x — installer.vars: block (P1, declarative variables)
    // Emitted when a var expression is malformed or the vars form a reference
    // cycle. A cycle is fatal (Error) — there is no safe evaluation order.
    public const string InvalidInstallerVar = "SIG0270";

    // SIG028x — installer.prerequisites: block (P5, gap G6)
    // Emitted (Error) when a prerequisite is missing name/detect/source, or an
    // https:// source omits the required sha256 integrity checksum.
    public const string InvalidPrerequisite = "SIG0280";

    // SIG029x — localization (P9, gap G10)
    // SIG0290 is FATAL: every runtime fallback bottoms out at `en`, so a map
    // without it has no defined rendering. Pack diagnostics reach manifest
    // authors, who do not build under this repo's TreatWarningsAsErrors — a
    // warning here would genuinely ship blank strings.
    public const string LocalizedTextMissingEnglish = "SIG0290";
    public const string InvalidLanguageTag = "SIG0291";

    // SIG0300 — installer.options.components: block (P10, gap G11, custom components)
    // Emitted (Error) when a custom component's name is not a bare identifier,
    // collides with a built-in component or a declared parameter, duplicates
    // another custom component, or the component omits its required label.
    public const string InvalidCustomComponent = "SIG0300";

    // SIG031x — machine-scope-only install steps (P11). T11.1-T11.3 add three
    // steps (scheduled_task_create, com_register, firewall_rule) that touch
    // machine-global state; each overrides InstallStep.RequiresMachineScope to
    // true. SIG0310 is FATAL: it fires when such a step appears anywhere in the
    // manifest (install_steps/pre_install/post_install/uninstall or any
    // installer.hooks phase) while installer.scope is not `machine` — `auto`
    // resolves to per-user scope by default, so it fails the guard too.
    public const string SystemStepRequiresMachineScope = "SIG0310";

    // SIG0292 — a LocalizedText map's per-language value is not a plain scalar
    // (e.g. a nested sequence/mapping under a language key). Fatal for the same
    // reason as SIG0290: the value silently collapses to "" otherwise, which is
    // the same silent-blank-rendering failure shape one language key at a time.
    public const string LocalizedTextValueNotScalar = "SIG0292";

    // SIG032x — update engine channel manifest (P12). Unlike the bands above,
    // these fire at UPDATE RUNTIME (inside the AOT wrapper/host, `/Update` mode),
    // not at pack time — there is no pack-time diagnostics list to append to, so
    // the runtime call path returns a typed parse/verify result carrying one of
    // these codes for the caller to log + map to a process exit code. The codes
    // stay the shared identifiers across both worlds (docs, logs, tests).
    //
    // SIG0320 (T12.1, this task): the fetched channel manifest JSON fails to
    // parse, is missing a required field (version/packageUrl/sha256), declares
    // a non-https packageUrl, or declares an unsupported schemaVersion.
    public const string MalformedChannelManifest = "SIG0320";

    // SIG0321 (reserved for T12.2): the channel manifest's detached ECDSA P-256
    // signature (fetched from `manifestUrl + ".sig"`) fails to verify against
    // `updates.signingKey`.
    public const string ChannelManifestSignatureInvalid = "SIG0321";

    // SIG0322 (T12.5): the web-installer's package URL could not be resolved.
    // Emitted at PACK TIME by `sigil pack --payload web` when `--package-url` is
    // missing, empty, or not https:// — pack refuses rather than stamping a stub
    // whose synthesized http_download step could never succeed. Also reserved
    // for the analogous install-time bootstrap failure (the stub could not
    // resolve/download the package at that URL).
    public const string WebInstallerPackageUrlUnresolved = "SIG0322";

    // SIG0323-SIG0326 — network trust on manifest-declared URLs, keys, and the
    // downloaded-binary policy (register rows R8, R14, R30, R45). Unlike
    // SIG0320-SIG0322 above, these fire at PACK TIME, in `ManifestParser`,
    // against the app manifest. They are one band because they share a subject
    // — what the update/parameter machinery may talk to and what it trusts —
    // not because they share a call path.

    // SIG0323 (R8): a `parameters.*.source.url` is not https://. The fetched
    // values become parameter values, which are substituted into step fields
    // (paths, registry coordinates, arguments) that execute elevated, so a
    // cleartext origin is an injection point into a privileged run. Mirrors
    // SIG0235's http_download stance; re-checked at install time in
    // `HttpOptionsLoader.LoadAsync`, because a URL built from tokens is not
    // knowable at pack time.
    public const string ParameterSourceInsecure = "SIG0323";

    // SIG0324 (R14): `updates.manifestUrl` is not https://. The schema's own
    // description said "HTTPS URL" while constraining only `format: uri`.
    // Code execution is still gated by the channel-manifest signature, so the
    // impact is cleartext leakage of app-id/version/channel plus a reliable
    // update-suppression DoS. Re-checked before the fetch at update runtime.
    public const string UpdateManifestUrlInsecure = "SIG0324";

    // SIG0325 (R30): `updates.signingKey` is not a base64-encoded X.509 SPKI
    // DER of an ECDSA P-256 PUBLIC key. The field was passed through
    // unvalidated, so `sigil init --template full`'s own private-key FILE PATH
    // packed cleanly and produced an installer whose every update attempt died
    // at SIG0321 — failing closed, but only after shipping.
    public const string UpdateSigningKeyInvalid = "SIG0325";

    // SIG0326 (R45): `installer.require_signed_downloads` is not one of the
    // declared policy values. The policy governs whether a binary this run
    // pulled off the network must be Authenticode-valid before it is launched
    // elevated, so an unrecognized value is refused rather than silently
    // falling back to the default.
    public const string RequireSignedDownloadsInvalid = "SIG0326";
}
