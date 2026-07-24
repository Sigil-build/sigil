using System;
using System.Text.Json;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Wrapper.Json;

namespace SigilBuild.Wrapper.Update;

/// <summary>
/// Parses + validates the channel manifest JSON fetched from
/// <c>updates.manifestUrl</c> at <c>/Update</c> time (P12, T12.1). Uses the
/// source-generated <see cref="ChannelManifestJsonContext"/> exclusively — no
/// reflection-based <see cref="JsonSerializer"/> overload, per the Native AOT
/// contract every wrapper-runtime assembly ships under.
/// </summary>
/// <remarks>
/// Does NOT verify the detached signature (T12.2) or compare versions against
/// the installed one (T12.3) — this is purely "is the fetched document a
/// well-formed, in-range channel manifest".
/// </remarks>
internal static class ChannelManifestParser
{
    /// <summary>
    /// The only <see cref="ChannelManifest.SchemaVersion"/> this runtime
    /// understands. Any other value — including 0, the default for a JSON
    /// document that omits the field — is rejected as SIG0320 rather than
    /// guessed at, so a future breaking schema bump fails loudly on an old
    /// installed runtime instead of silently misreading fields.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Parse + validate raw channel-manifest JSON text. Malformed JSON, a
    /// missing required field (<c>version</c>/<c>packageUrl</c>/<c>sha256</c>),
    /// a non-<c>https://</c> <c>packageUrl</c>, or an unsupported
    /// <c>schemaVersion</c> all return a <see cref="ChannelManifestParseResult.Failed"/>
    /// carrying <see cref="DiagnosticCodes.MalformedChannelManifest"/> (SIG0320).
    /// </summary>
    public static ChannelManifestParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ChannelManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(json, ChannelManifestJsonContext.Default.ChannelManifest);
        }
        catch (JsonException ex)
        {
            return Malformed($"channel manifest is not valid JSON ({ex.Message})");
        }

        if (manifest is null)
        {
            return Malformed("channel manifest JSON deserialized to null");
        }

        return Validate(manifest);
    }

    private static ChannelManifestParseResult Validate(ChannelManifest manifest)
    {
        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            return Malformed(
                $"unsupported schemaVersion {manifest.SchemaVersion} (expected {CurrentSchemaVersion})");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            return Malformed("missing required field 'version'");
        }

        if (string.IsNullOrWhiteSpace(manifest.PackageUrl))
        {
            return Malformed("missing required field 'packageUrl'");
        }

        // Mirrors SIG0235's http_download insecure-URL stance (P4), applied at
        // update runtime instead of pack time: a plain prefix check, not a full
        // Uri parse, since a channel manifest that can't even spell "https://"
        // correctly is already malformed.
        if (!manifest.PackageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Malformed($"packageUrl must be https:// (got '{manifest.PackageUrl}')");
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            return Malformed("missing required field 'sha256'");
        }

        return ChannelManifestParseResult.Ok(manifest);
    }

    private static ChannelManifestParseResult Malformed(string detail) =>
        ChannelManifestParseResult.Failed(
            DiagnosticCodes.MalformedChannelManifest,
            $"Malformed channel manifest: {detail}.");
}
