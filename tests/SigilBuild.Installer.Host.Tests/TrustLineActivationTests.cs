namespace SigilBuild.Installer.Host.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using Xunit;

/// <summary>
/// Register row R48 — the trust-line lookup must not block the wizard's UI thread.
/// </summary>
/// <remarks>
/// <c>InstallerTrustLoader.ResolveFromSelf</c> is <c>WinVerifyTrust</c> with whole-chain
/// revocation checking: a network operation, measured at 335 ms on the happy path
/// (online, warm certificate cache, embedded-signed target). It used to run inline in
/// <c>App.OnFrameworkInitializationCompleted</c> while the first window was being built,
/// so the wizard could not paint until it returned. These tests assert the property that
/// fixes that — the caller is not made to wait — without measuring anything, which is
/// deliberate: a timed run on this box would measure a warm cache.
/// </remarks>
public class TrustLineActivationTests
{
    [Fact]
    public async Task Resolving_the_trust_line_does_not_block_the_caller()
    {
        var tokens = new BrandTokens();
        using var resolveMayFinish = new ManualResetEventSlim(false);
        using var resolveHasStarted = new ManualResetEventSlim(false);

        // Stands in for a revocation lookup against an unreachable CRL distribution
        // point: it does not return until told to.
        var pending = TrustLineActivation.BeginAsync(
            tokens,
            resolve: () =>
            {
                resolveHasStarted.Set();
                resolveMayFinish.Wait(TimeSpan.FromSeconds(30));
                return "Signed by Acme, Inc.";
            },
            post: action => action());

        // The call returned while the lookup is still in flight — this is the whole fix.
        resolveHasStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue(
            "the lookup must actually have been started on another thread");
        pending.IsCompleted.Should().BeFalse(
            "the UI thread must not be inside WinVerifyTrust — a revocation lookup that " +
            "cannot reach its responder holds the wizard's first paint for as long as it " +
            "takes to time out");
        tokens.TrustLine.Should().BeNull(
            "no line is the safe default while the answer is unknown; the trust line is " +
            "additive assurance, so its absence claims nothing");

        resolveMayFinish.Set();
        await pending;

        tokens.TrustLine.Should().Be("Signed by Acme, Inc.");
        tokens.HasTrustLine.Should().BeTrue();
    }

    [Fact]
    public async Task The_resolved_line_reaches_the_binding()
    {
        var tokens = new BrandTokens();
        var changed = new List<string?>();
        tokens.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await TrustLineActivation.BeginAsync(
            tokens, resolve: () => "Signed by Acme, Inc.", post: action => action());

        changed.Should().Contain(nameof(BrandTokens.TrustLine));
        changed.Should().Contain(
            nameof(BrandTokens.HasTrustLine),
            "the XAML binds IsVisible to HasTrustLine, which is derived — without its own " +
            "notification the line resolves and never appears");
    }

    [Fact]
    public async Task A_lookup_that_throws_renders_as_no_line_rather_than_a_crashed_wizard()
    {
        var tokens = new BrandTokens();

        await TrustLineActivation.BeginAsync(
            tokens,
            resolve: () => throw new InvalidOperationException("WinVerifyTrust exploded"),
            post: action => action());

        tokens.TrustLine.Should().BeNull();
        tokens.HasTrustLine.Should().BeFalse();
    }
}
