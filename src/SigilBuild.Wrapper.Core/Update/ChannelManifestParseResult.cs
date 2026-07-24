namespace SigilBuild.Wrapper.Update;

/// <summary>
/// Typed outcome of <see cref="ChannelManifestParser.Parse(string)"/>. The
/// update runtime call path (T12.3) has no pack-time diagnostics list to
/// append to, so a malformed manifest is surfaced as a result the caller logs
/// and maps to a process exit code, rather than a thrown exception — mirroring
/// <c>StepResult</c> / <c>EngineResult</c> in <c>SigilBuild.Wrapper.Engine</c>.
/// </summary>
internal sealed record ChannelManifestParseResult(
    bool Success,
    ChannelManifest? Manifest,
    string? DiagnosticCode,
    string? Error)
{
    public static ChannelManifestParseResult Ok(ChannelManifest manifest) =>
        new(true, manifest, null, null);

    public static ChannelManifestParseResult Failed(string diagnosticCode, string error) =>
        new(false, null, diagnosticCode, error);
}
