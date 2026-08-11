namespace SigilBuild.Wrapper.Engine;

using System;
using System.Threading;

/// <summary>
/// The Authenticode gate that stands immediately in front of every
/// <c>Process.Start</c> of a binary this run pulled off the network — a prerequisite
/// installer, an update package, a web-stub payload (register row R11).
/// </summary>
/// <remarks>
/// <para>
/// <b>What was wrong.</b> <see cref="AuthenticodeVerifier.VerifyFile"/> existed, was
/// AOT-clean, and had exactly one caller in the tree: the code that renders the
/// wizard's cosmetic "Signed by …" line. Nothing downloaded was signature-checked
/// before it was executed, elevated. SHA-256 was the sole gate — and a SHA-256 pinned
/// in a manifest only ever says "these are the bytes the manifest names", which is
/// worth nothing against an origin that serves different bytes and the matching digest,
/// and nothing at all in the instant between a verification and a launch. The staging
/// work in this lane closed those instants; this is the second line that means losing
/// one of those races is no longer immediately fatal.
/// </para>
/// <para>
/// <b>Fail closed, with one deliberate hole.</b> Unsigned redistributables are common
/// and legitimate, so a prerequisite may declare <c>allow_unsigned: true</c> and be
/// launched without a signature. That opt-out covers "no usable signature" —
/// <see cref="AuthenticodeStatus.NoSignature"/> and <see cref="AuthenticodeStatus.Invalid"/>
/// — because from a trust standpoint a broken or untrusted signature is no better and
/// no worse than none at all, and the manifest's SHA-256 is still enforced either way.
/// It does NOT cover <see cref="AuthenticodeStatus.Revoked"/>: that is not an absence
/// of evidence, it is a positive statement by a certificate authority (or by Microsoft's
/// disallowed list) that the key is bad, and no manifest flag overrides it.
/// </para>
/// <para>
/// <b>Why <see cref="AuthenticodeStatus.RevocationUnavailable"/> proceeds.</b> Refusing
/// it would mean an installer that cannot reach a CRL distribution point — an air-gapped
/// network, a captive portal, a locked-down enterprise egress — cannot install anything,
/// which is a far more likely outcome than the attack it would prevent, and the plan is
/// explicit that anchoring which breaks real installs is worse than the bug it closes.
/// It proceeds, loudly: the warning line says what could not be established rather than
/// letting silence read as confirmation.
/// </para>
/// </remarks>
internal static class DownloadedBinaryTrust
{
    /// <summary>
    /// Test seam: forces <see cref="RequiredForThisArtifact"/> for the scope of the
    /// returned token. <see cref="AsyncLocal{T}"/>, not a plain static, so the override
    /// is confined to the test that set it and still flows into the engine's async work
    /// — xUnit runs collections in parallel. Same shape and rationale as
    /// <c>SecureStaging.UseSitingForTesting</c>.
    /// </summary>
    private static readonly AsyncLocal<bool?> RequirementOverride = new();

    /// <summary>-1 unknown, 0 no, 1 yes. The self-blob read is done once per process.</summary>
    private static int _selfDeclaresSigning = -1;

    /// <summary>
    /// Whether THIS artifact requires the binaries it downloads to be signed — i.e.
    /// whether its own manifest declared a <c>sign</c> block, read from the running
    /// exe's embedded blob.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The signal is pack-time and lives inside the signed artifact, which is what makes
    /// it usable as policy: it is the same <c>SignDeclared</c> that gates the wizard's
    /// trust line, so an installer that claims a signed provenance is held to it for
    /// everything it fetches and runs, and one that never claimed it is not.
    /// </para>
    /// <para>
    /// The alternative — requiring signatures unconditionally — was rejected because it
    /// would break every unsigned author's <c>/Update</c> path and web stub outright,
    /// with no manifest knob to reach for, while adding nothing: an unsigned stub's own
    /// bytes are unprotected anyway, so demanding a signature on what it downloads is
    /// ceremony. Prerequisites are NOT gated on this — a redistributable from a third
    /// party is worth checking regardless of who built the installer around it, and it
    /// has its own per-prerequisite opt-out.
    /// </para>
    /// </remarks>
    internal static bool RequiredForThisArtifact => RequirementOverride.Value ?? SelfDeclaresSigning;

    /// <summary>
    /// Said out loud whenever <see cref="RequiredForThisArtifact"/> is false at a launch
    /// site that would otherwise have gated. The inference above is defensible; making it
    /// SILENTLY is not. An author reads "downloaded binaries are Authenticode-checked",
    /// packs without a <c>sign</c> block — the default for anyone not yet code-signing —
    /// and two thirds of that sentence is false for their artifact, with no manifest knob,
    /// no diagnostic and no log line to tell them. This is the log line.
    /// </summary>
    internal const string DisarmedNotice =
        "signature: this artifact declared no `sign` block — downloaded binaries are NOT " +
        "Authenticode-checked before they are launched; the sha256 is the only gate";

    private static bool SelfDeclaresSigning
    {
        get
        {
            var cached = Volatile.Read(ref _selfDeclaresSigning);
            if (cached < 0)
            {
                cached = (WrapperBlob.LoadBrandFromSelf()?.SignDeclared ?? false) ? 1 : 0;
                Volatile.Write(ref _selfDeclaresSigning, cached);
            }
            return cached == 1;
        }
    }

    /// <summary>Test seam (internal): pin <see cref="RequiredForThisArtifact"/>. Not for production use.</summary>
    internal static IDisposable RequireForTesting(bool required)
    {
        var previous = RequirementOverride.Value;
        RequirementOverride.Value = required;
        return new RestoreRequirement(previous);
    }

    private sealed class RestoreRequirement : IDisposable
    {
        private readonly bool? _previous;

        public RestoreRequirement(bool? previous) => _previous = previous;

        public void Dispose() => RequirementOverride.Value = _previous;
    }

    /// <summary>
    /// The pure policy. Returns the refusal reason, or <c>null</c> to allow, plus an
    /// optional line to report either way. Separated from the P/Invoke so every branch
    /// — including the revoked and the offline ones, which no fixture on a developer
    /// box can produce — is exercised by ordinary unit tests on any host.
    /// </summary>
    /// <param name="status">What <c>WinVerifyTrust</c> concluded.</param>
    /// <param name="what">How to name the binary in the message, e.g. <c>prerequisite 'VC++ Redist'</c>.</param>
    /// <param name="allowUnsigned">The declared opt-out for a legitimately unsigned redistributable.</param>
    internal static (string? Refusal, string? Report, bool IsError) Decide(
        AuthenticodeStatus status, string what, bool allowUnsigned) => status switch
        {
            // Nothing was examined — off Windows, where no wrapper ships and where this
            // assembly is nonetheless built and unit-tested. Not a refusal: a verdict was
            // never sought, so treating it as a failed one would fail every non-Windows
            // unit test for reasons that have nothing to do with trust.
            AuthenticodeStatus.NotEvaluated => (null, null, false),

            AuthenticodeStatus.Trusted =>
                (null, $"signature: {what} is Authenticode-valid", false),

            AuthenticodeStatus.RevocationUnavailable => (
                null,
                $"signature: {what} is Authenticode-valid, but its revocation status could NOT be " +
                "established (no reachable CRL/OCSP responder) — proceeding, but this is not a " +
                "confirmation that the certificate is still valid",
                true),

            // No opt-out. An absence of evidence can be waived; a positive statement that
            // the key is bad cannot.
            AuthenticodeStatus.Revoked => (
                $"{what} is signed with a REVOKED or explicitly distrusted certificate; refusing to run it",
                null, false),

            AuthenticodeStatus.NoSignature when allowUnsigned => (
                null,
                $"signature: {what} is unsigned and was declared allow_unsigned — running it on the " +
                "strength of its sha256 alone",
                true),

            AuthenticodeStatus.NoSignature => (
                $"{what} carries no Authenticode signature; refusing to run a downloaded binary that " +
                "cannot be authenticated. Declare allow_unsigned: true on this prerequisite if it is a " +
                "genuinely unsigned redistributable",
                null, false),

            AuthenticodeStatus.Invalid when allowUnsigned => (
                null,
                $"signature: {what} has an Authenticode signature that does not establish trust and was " +
                "declared allow_unsigned — running it on the strength of its sha256 alone",
                true),

            _ => (
                $"{what} has an Authenticode signature that does not establish trust (tampered, expired, " +
                "or chaining to no trusted root); refusing to run it",
                null, false),
        };

    /// <summary>
    /// The entry point for the two launch sites whose gate is conditioned on
    /// <see cref="RequiredForThisArtifact"/> — the update package and a web-stub payload.
    /// Gates when the artifact declared signing; otherwise reports
    /// <see cref="DisarmedNotice"/> and allows.
    /// </summary>
    /// <remarks>
    /// Both branches go through here so that "the gate did not run" is a single, named,
    /// reported event rather than a condition each call site writes for itself — the shape
    /// that let it be silent in the first place. There is no <c>allowUnsigned</c>
    /// parameter: neither of these sites has a per-item manifest knob, and the
    /// artifact-level answer is the one being consulted.
    /// </remarks>
    internal static string? RefusalForArtifactDownload(string path, string what, Action<string, bool> report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!RequiredForThisArtifact)
        {
            report(DisarmedNotice, true);
            return null;
        }

        return Refusal(path, what, allowUnsigned: false, report);
    }

    /// <summary>
    /// Evaluate <paramref name="path"/> immediately before it is launched, report the
    /// informational or warning line on <paramref name="report"/>, and return the
    /// refusal reason — or <c>null</c> when the launch may go ahead. The caller surfaces
    /// the refusal in whatever typed form its own failures take.
    /// </summary>
    internal static string? Refusal(string path, string what, bool allowUnsigned, Action<string, bool> report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var (refusal, line, isError) = Decide(AuthenticodeVerifier.VerifyFileStatus(path), what, allowUnsigned);
        if (line is not null)
        {
            report(line, isError);
        }
        return refusal;
    }
}
