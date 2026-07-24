namespace SigilBuild.Wrapper.Update;

/// <summary>
/// Typed outcome of <see cref="ChannelManifestVerifier.Verify(byte[], string?, string?)"/>.
/// Sibling of <see cref="ChannelManifestParseResult"/> (T12.1): same
/// success/diagnosticCode/error shape, but signature verification has no
/// payload to carry on success — <see cref="ChannelManifestParseResult"/>
/// requires a non-null <see cref="ChannelManifest"/> on <c>Ok</c>, which
/// doesn't fit here — so this is its own typed result rather than a forced
/// reuse.
/// </summary>
internal sealed record ChannelManifestVerifyResult(
    bool Success,
    string? DiagnosticCode,
    string? Error)
{
    public static readonly ChannelManifestVerifyResult Ok = new(true, null, null);

    public static ChannelManifestVerifyResult Failed(string diagnosticCode, string error) =>
        new(false, diagnosticCode, error);
}
