namespace SigilBuild.Wrapper.Update;

/// <summary>
/// The signed channel manifest fetched from the app manifest's
/// <c>updates.manifestUrl</c> at <c>/Update</c> time (P12). Describes the
/// latest available package for one update channel.
/// </summary>
/// <remarks>
/// <para>
/// <c>channel</c> and <c>signingKey</c> are properties of the APP manifest's
/// <c>updates:</c> block (<see cref="SigilBuild.Core.Manifest.UpdatesSection"/>),
/// not of this document — a channel manifest describes one already-selected
/// channel's latest package, it does not name itself.
/// </para>
/// <para>
/// <b>Signature (T12.2 implements verification; this task only fixes the
/// representation so T12.2 has a stable contract to consume):</b> the manifest
/// is distributed as two sibling HTTP resources — the JSON body at
/// <c>manifestUrl</c>, and a detached signature at <c>manifestUrl + ".sig"</c>.
/// The <c>.sig</c> resource is the base64 encoding of a raw IEEE P1363
/// (r||s, 64-byte) ECDSA P-256 signature — i.e. exactly what
/// <see cref="System.Security.Cryptography.ECDsa.SignData(byte[], System.Security.Cryptography.HashAlgorithmName)"/>
/// returns with the default <see cref="System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation"/>
/// format — computed over the exact bytes of the manifest JSON (no
/// canonicalization step; the fetched bytes are hashed as-is with SHA-256).
/// <see cref="SigilBuild.Core.Manifest.UpdatesSection.SigningKey"/> is the
/// corresponding ECDSA P-256 public key, base64-encoded SubjectPublicKeyInfo
/// (X.509 SPKI, DER) — i.e. what
/// <see cref="System.Security.Cryptography.ECDsa.ExportSubjectPublicKeyInfo"/>
/// returns, base64-encoded. Both choices use only BCL surface
/// (<see cref="System.Security.Cryptography.ECDsa.ImportSubjectPublicKeyInfo"/> /
/// <see cref="System.Security.Cryptography.ECDsa.VerifyData(byte[], byte[], System.Security.Cryptography.HashAlgorithmName)"/>)
/// so T12.2's verifier needs no ASN.1/DER hand-rolling for the signature itself,
/// only the well-trodden SPKI import path.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">
/// Forward-compat schema version. <see cref="ChannelManifestParser.CurrentSchemaVersion"/>
/// is the only value this runtime accepts; any other value (including 0, the
/// default for an omitted field) is rejected as SIG0320 rather than guessed at.
/// </param>
/// <param name="Version">The available package's dotted version string. Required.</param>
/// <param name="PackageUrl">
/// HTTPS URL of the full update package. Required; rejected (SIG0320) if it
/// does not start with <c>https://</c>, mirroring P4's SIG0235 insecure-URL
/// stance for <c>http_download</c> steps, applied here at update runtime.
/// </param>
/// <param name="Sha256">Hex/base64 integrity checksum of the package at <see cref="PackageUrl"/>. Required.</param>
/// <param name="MinFromVersion">
/// Minimum installed version eligible to update from, or <c>null</c> if any
/// installed version may update to this one (e.g. a full package with no
/// delta-from floor).
/// </param>
internal sealed record ChannelManifest(
    int SchemaVersion,
    string Version,
    string PackageUrl,
    string Sha256,
    string? MinFromVersion = null);
