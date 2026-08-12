using System;
using System.Threading.Tasks;

namespace SigilBuild.Installer.Host.Branding;

/// <summary>
/// Register row R48 — resolve the "Signed by …" trust line WITHOUT blocking the wizard's
/// UI thread.
/// </summary>
/// <remarks>
/// <para>
/// <c>InstallerTrustLoader.ResolveFromSelf</c> calls <c>WinVerifyTrust</c> with
/// whole-chain revocation checking (S1/R17). That is a network operation: it fetches
/// CRLs and talks to OCSP responders. It used to run inline in
/// <c>App.OnFrameworkInitializationCompleted</c>, while the first window was being
/// constructed — so the wizard could not paint until it returned.
/// </para>
/// <para>
/// <b>335 ms on the happy path</b> — online, warm OS certificate cache, embedded-signed
/// target — with the second and third runs at 6–9 ms. That already exceeds the ~100 ms
/// at which a UI reads as unresponsive, and every condition that makes it worse (cold
/// cache, captive portal, unreachable CRL distribution point) moves in one direction
/// only; the documented CRL timeout is measured in seconds. That number settles the fix;
/// the worst case is a separate measurement and is deliberately not claimed here.
/// </para>
/// <para>
/// The trust line is <em>additive</em> assurance — its absence means "no claim is being
/// made", never "this is forged" — so rendering nothing for a few hundred milliseconds
/// and then showing the line is safe in a way that the reverse would not be. Nothing else
/// gates on it.
/// </para>
/// </remarks>
public static class TrustLineActivation
{
    /// <summary>
    /// Run <paramref name="resolve"/> off the calling thread and hand the result back
    /// through <paramref name="post"/> — the UI-thread marshaller — for assignment to
    /// <paramref name="tokens"/>. Returns as soon as the work is queued: the caller is
    /// the UI thread and must not wait.
    /// </summary>
    /// <returns>
    /// The task that completes once the trust line has been posted. Tests await it; the
    /// wizard does not.
    /// </returns>
    public static Task BeginAsync(BrandTokens tokens, Func<string?> resolve, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(post);

        return Task.Run(() =>
        {
            string? line;
#pragma warning disable CA1031 // A trust line that cannot be resolved renders as no line, never as a crashed wizard.
            try
            {
                line = resolve();
            }
            catch (Exception)
            {
                line = null;
            }
#pragma warning restore CA1031

            // Back to the UI thread: PropertyChanged drives an Avalonia binding.
            post(() => tokens.TrustLine = line);
        });
    }
}
