using System;
using System.Diagnostics.CodeAnalysis;

namespace SigilBuild.Wrapper.Core.Localization;

/// <summary>
/// The resolved chrome language for this install session. Set exactly once at
/// session start (see the resolver) before any UI is constructed, and immutable
/// thereafter — which is what makes the generated <c>S</c> static accessor legal
/// in XAML <c>{x:Static}</c>.
/// </summary>
/// <remarks>
/// The guard is deliberately asymmetric (design §3.2). A read before
/// initialization means someone reordered startup, and the natural consequence
/// would be a silent wrong-language render — invisible to tests and users alike.
/// Debug throws so the test suite fails loudly; Release falls back to English and
/// logs, so a shipped installer degrades rather than dies.
/// </remarks>
public static class SessionLanguage
{
    private static Lang? _current;

    public static Lang Current
    {
        get
        {
            if (_current is { } value)
            {
                return value;
            }
#if DEBUG
            throw new InvalidOperationException(
                "SessionLanguage.Current read before the session language was resolved. " +
                "Resolution must run at session start, before any UI is constructed.");
#else
            OnUninitializedRead?.Invoke();
            return Lang.En;
#endif
        }
    }

    /// <summary>Raised on a Release-mode read before <see cref="Set"/>. Wired to the install log.</summary>
    public static Action? OnUninitializedRead { get; set; }

    public static bool IsSet => _current is not null;

    public static void Set(Lang lang) => _current = lang;

    [SuppressMessage("Usage", "CA2255", Justification = "Test-only reset of static session state.")]
    internal static void SetForTesting(Lang lang) => _current = lang;

    internal static void ResetForTesting() => _current = null;
}
