namespace SigilBuild.Wrapper.Tests.Engine;

using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using Xunit;

/// <summary>
/// Register rows R45 (the downloaded-binary signature policy is DECLARED, not inferred)
/// and R46 (an unestablished revocation status can be made a refusal).
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests do NOT compile at the parent commit</b>, and that is stated here
/// rather than left to be discovered: they name
/// <see cref="RequireSignedDownloads"/>, which R45 introduces. The parent-failing proof
/// for R45 is the SIG0326 parse test in <c>NetworkTrustParseTests</c>, which uses only
/// string literals; what follows is the behavioural half that can only exist once the
/// type does.
/// </para>
/// <para>
/// <b>Nothing here touches the host.</b> <see cref="DownloadedBinaryTrust.Decide"/> is a
/// pure function of an enum, a string and a policy — no file, no P/Invoke, no process.
/// </para>
/// </remarks>
public sealed class DownloadPolicyTests
{
    private const string What = "the downloaded 2.0.0 installer";

    // ── R45: the default is exactly the behaviour it replaces ─────────────────

    [Fact]
    public void The_default_policy_is_sign_declared()
    {
        // The whole point of the default: adding this field changes no existing manifest.
        new InstallerSection(Brand: null).RequireSignedDownloads
            .Should().Be(RequireSignedDownloads.SignDeclared);
    }

    [Fact]
    public void Under_the_default_policy_an_unreachable_responder_is_a_warning_not_a_refusal()
    {
        var (refusal, report, isError) = DownloadedBinaryTrust.Decide(
            AuthenticodeStatus.RevocationUnavailable, What, allowUnsigned: false);

        refusal.Should().BeNull(
            "refusing by default would mean an installer behind a captive portal, on an air-gapped " +
            "network, or inside a locked-down enterprise egress cannot install anything — which is a " +
            "far more likely outcome than the attack it would prevent");
        report.Should().Contain("could NOT be established");
        isError.Should().BeTrue("it must be loud even though it proceeds");
    }

    [Fact]
    public void Always_policy_leaves_the_revocation_verdict_alone()
    {
        // `always` is about WHETHER the gate runs, not about how strict it is once it does.
        var (refusal, _, _) = DownloadedBinaryTrust.Decide(
            AuthenticodeStatus.RevocationUnavailable, What, allowUnsigned: false,
            RequireSignedDownloads.Always);

        refusal.Should().BeNull();
    }

    // ── R46: the opt-in hard fail ─────────────────────────────────────────────

    [Fact]
    public void Under_always_verified_revocation_an_unreachable_responder_is_a_refusal()
    {
        var (refusal, _, _) = DownloadedBinaryTrust.Decide(
            AuthenticodeStatus.RevocationUnavailable, What, allowUnsigned: false,
            RequireSignedDownloads.AlwaysVerifiedRevocation);

        refusal.Should().NotBeNull(
            "R46: `RevocationUnavailable` not being a refusal means anyone who can blackhole two " +
            "hostnames suppresses revocation of a stolen signing key; a publisher who knows their " +
            "audience is online must be able to say so");
        refusal.Should().Contain("revocation");
    }

    [Fact]
    public void A_trusted_binary_is_still_allowed_under_the_strictest_policy()
    {
        // The over-refusal control: the strict policy must not refuse a good binary.
        var (refusal, report, _) = DownloadedBinaryTrust.Decide(
            AuthenticodeStatus.Trusted, What, allowUnsigned: false,
            RequireSignedDownloads.AlwaysVerifiedRevocation);

        refusal.Should().BeNull();
        report.Should().Contain("Authenticode-valid");
    }

    [Fact]
    public void NotEvaluated_is_still_not_a_refusal_under_the_strictest_policy()
    {
        // Off Windows no verdict is ever sought. Treating "never asked" as "failed" would
        // fail every non-Windows unit test for reasons that have nothing to do with trust.
        var (refusal, _, _) = DownloadedBinaryTrust.Decide(
            AuthenticodeStatus.NotEvaluated, What, allowUnsigned: false,
            RequireSignedDownloads.AlwaysVerifiedRevocation);

        refusal.Should().BeNull();
    }

    [Fact]
    public void Revoked_is_a_refusal_under_every_policy()
    {
        // A positive statement that the key is bad is never waivable, by any setting.
        foreach (var policy in new[]
                 {
                     RequireSignedDownloads.SignDeclared,
                     RequireSignedDownloads.Always,
                     RequireSignedDownloads.AlwaysVerifiedRevocation,
                 })
        {
            var (refusal, _, _) = DownloadedBinaryTrust.Decide(
                AuthenticodeStatus.Revoked, What, allowUnsigned: true, policy);

            refusal.Should().NotBeNull(
                "a revoked certificate is a positive statement by a CA, not an absence of evidence, " +
                "so no policy value and no allow_unsigned opt-out may override it (policy {0})",
                policy);
        }
    }

    // ── R45: `always` arms the gate for an artifact that declares no signing ──

    [Fact]
    public void Always_arms_the_gate_for_an_artifact_that_declared_no_sign_block()
    {
        // The row's actual complaint: `SignDeclared` answers "did this publisher configure
        // signing for their own output", and that was being used as a proxy for "should
        // downloads be verified". A publisher who signs nothing got no verification on
        // anything they downloaded and ran elevated.
        using (DownloadedBinaryTrust.RequireForTesting(false))
        using (DownloadedBinaryTrust.UsePolicyForTesting(RequireSignedDownloads.Always))
        {
            DownloadedBinaryTrust.RequiredForThisArtifact.Should().BeTrue(
                "an explicit `always` must arm the gate even though this artifact declares no " +
                "`sign` block of its own");
        }
    }

    [Fact]
    public void Sign_declared_policy_still_defers_to_the_artifacts_own_sign_block()
    {
        using (DownloadedBinaryTrust.UsePolicyForTesting(RequireSignedDownloads.SignDeclared))
        {
            using (DownloadedBinaryTrust.RequireForTesting(false))
            {
                DownloadedBinaryTrust.RequiredForThisArtifact.Should().BeFalse();
            }

            using (DownloadedBinaryTrust.RequireForTesting(true))
            {
                DownloadedBinaryTrust.RequiredForThisArtifact.Should().BeTrue();
            }
        }
    }
}
