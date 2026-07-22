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

    // SIG0292 — a LocalizedText map's per-language value is not a plain scalar
    // (e.g. a nested sequence/mapping under a language key). Fatal for the same
    // reason as SIG0290: the value silently collapses to "" otherwise, which is
    // the same silent-blank-rendering failure shape one language key at a time.
    public const string LocalizedTextValueNotScalar = "SIG0292";
}
