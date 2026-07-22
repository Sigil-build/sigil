using System;
using System.Collections.Generic;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Wrapper.Core.Localization;

/// <summary>
/// Resolves the session's language preferences and matches surfaces against them.
/// See docs/plan/feature-parity/P9-DESIGN-localization.md §4.
/// </summary>
public static class LanguageResolver
{
    private static readonly string[] EnOnly = { "en" };

    /// <summary>
    /// The chain: installer.language (fixed) -> /lang -> OS list -> en.
    /// Returns an ORDERED list, not a single tag: the OS reports preferences in
    /// order, and a user whose list is [de-DE, uk-UA] has said they read Ukrainian
    /// better than English. Taking only the first entry would discard that.
    /// </summary>
    public static IReadOnlyList<string> Preferences(
        string? manifestLanguage, string? langFlag, IReadOnlyList<string> osPreferences)
    {
        if (LanguageTag.IsValid(manifestLanguage))
        {
            return new[] { manifestLanguage! };
        }

        if (LanguageTag.IsValid(langFlag))
        {
            return new[] { langFlag! };
        }

        return osPreferences.Count > 0 ? osPreferences : EnOnly;
    }

    /// <summary>
    /// Ordinal-only best match. No ICU, no CultureInfo. Returns "en" when nothing
    /// matches — total for manifest maps because SIG0290 makes an en-less map a
    /// pack-time error.
    /// </summary>
    public static string Match(IReadOnlyList<string> preferences, IReadOnlyCollection<string> available)
    {
        foreach (var pref in preferences)
        {
            foreach (var a in available)
            {
                if (string.Equals(a, pref, StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }

            var primary = PrimarySubtag(pref);

            foreach (var a in available)
            {
                if (string.Equals(a, primary, StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }

            // Ordinal-first among same-primary candidates, purely for determinism:
            // de -> {de-CH, de-AT} must resolve identically on every machine.
            string? best = null;
            foreach (var a in available)
            {
                if (!string.Equals(PrimarySubtag(a), primary, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (best is null || string.CompareOrdinal(a, best) < 0)
                {
                    best = a;
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return "en";
    }

    /// <summary>
    /// Matches against the chrome catalog's language set. Never returns Lang.Pseudo:
    /// no catalog declares that tag and "pseudo" fails LanguageTag.IsValid.
    /// </summary>
    /// <remarks>
    /// Both the tag list and the mapping are GENERATED from Localization/Strings.*.txt
    /// (ChromeCatalog). Nothing here is hand-maintained, so adding Strings.de.txt wires
    /// the language end-to-end with no code edit — which is what ADR-008 §4's
    /// "languages ship as content contributions" rule requires. A hardcoded list here
    /// would make a new catalog file compile but stay unreachable, with no failing test.
    /// </remarks>
    public static Lang MatchChrome(IReadOnlyList<string> preferences) =>
        ChromeCatalog.FromTag(Match(preferences, ChromeCatalog.Tags));

    private static string PrimarySubtag(string tag)
    {
        var dash = tag.IndexOf('-');
        return dash < 0 ? tag : tag.Substring(0, dash);
    }
}
