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

    // SIG024x — installer.screens: block (T9, declared custom screens)
    public const string UnknownScreenParameterRef = "SIG0240";
    public const string InvalidScreenWhenExpression = "SIG0241";
    public const string InvalidScreenTitleToken = "SIG0242";
}
