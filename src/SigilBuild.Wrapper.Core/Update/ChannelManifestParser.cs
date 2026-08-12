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

        // R13: freshness. These are REQUIRED, not optional — an optional freshness
        // field is defeated by replaying a correctly signed manifest that predates
        // it, which is the exact attack. A manifest with no issuedAt is therefore
        // malformed rather than "unbounded".
        if (string.IsNullOrWhiteSpace(manifest.IssuedAt))
        {
            return Malformed(
                "missing required field 'issuedAt' — a manifest with no issue time cannot be " +
                "checked for freshness and is indistinguishable from a replay");
        }

        if (!TryParseTimestamp(manifest.IssuedAt, out _))
        {
            return Malformed($"'issuedAt' is not an ISO-8601 timestamp (got '{manifest.IssuedAt}')");
        }

        if (string.IsNullOrWhiteSpace(manifest.ExpiresAt))
        {
            return Malformed(
                "missing required field 'expiresAt' — a manifest with no expiry stays actionable " +
                "forever, which is what makes a freeze attack possible");
        }

        if (!TryParseTimestamp(manifest.ExpiresAt, out _))
        {
            return Malformed($"'expiresAt' is not an ISO-8601 timestamp (got '{manifest.ExpiresAt}')");
        }

        if (manifest.Sequence is null)
        {
            return Malformed(
                "missing required field 'sequence' — without it a manifest inside its validity " +
                "window can be rolled back to an earlier one");
        }

        if (manifest.Sequence < 0)
        {
            return Malformed($"'sequence' must be a non-negative integer (got {manifest.Sequence})");
        }

        return ChannelManifestParseResult.Ok(manifest);
    }

    /// <summary>
    /// The exact timestamp formats a channel manifest may use: ISO-8601 with an explicit
    /// UTC <c>Z</c> or a numeric offset, with or without fractional seconds.
    /// </summary>
    /// <remarks>
    /// <b>Exact formats, not <c>TryParse</c>.</b> The lenient parser accepts
    /// <c>01/02/2026</c> and resolves it by culture convention — two different days
    /// depending on who is reading. A validity window is a security boundary, and a
    /// security boundary whose meaning depends on the reader's locale is not one. An
    /// explicit offset is likewise required rather than assumed: "midnight, somewhere"
    /// is up to 26 hours of ambiguity at each end of the window.
    /// </remarks>
    private static readonly string[] TimestampFormats =
    {
        // Explicit numeric offset — what DateTimeOffset.ToString("O") emits.
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-ddTHH:mm:sszzz",
        // Literal Z. Note this cannot be spelled with "K", which also matches the EMPTY
        // string and would therefore silently admit a zone-less "2026-01-01T00:00:00".
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-ddTHH:mm:ss'Z'",
    };

    /// <summary>
    /// Parse an ISO-8601 timestamp from a channel manifest, strictly. The result is
    /// normalized to UTC, because the comparison the freshness gate makes is against
    /// <see cref="System.DateTimeOffset.UtcNow"/>.
    /// </summary>
    internal static bool TryParseTimestamp(string? value, out DateTimeOffset parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        return DateTimeOffset.TryParseExact(
            value.Trim(),
            TimestampFormats,
            System.Globalization.CultureInfo.InvariantCulture,
            // AssumeUniversal only bites for the literal-Z formats, which are universal by
            // construction; the zzz formats carry their own offset and it wins.
            System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out parsed);
    }

    private static ChannelManifestParseResult Malformed(string detail) =>
        ChannelManifestParseResult.Failed(
            DiagnosticCodes.MalformedChannelManifest,
            $"Malformed channel manifest: {detail}.");
}
