using System;
using System.Collections.Generic;
using System.Linq;

namespace SigilBuild.Core.Manifest;

/// <summary>
/// Manifest text that may be authored either as a plain string or as a
/// <c>{ en: ..., uk: ... }</c> map. A plain string normalizes to <c>{"en": value}</c>
/// at parse time, so the map is the only shape that exists at runtime and no
/// consumer branches on "string or map".
/// </summary>
/// <remarks>
/// Picking a language is deliberately NOT a method here: this record is manifest
/// data shared with pack time, while matching belongs next to the resolver in
/// SigilBuild.Wrapper.Core/Localization. Core carries the map; Wrapper.Core
/// resolves it. See design §5.1.
/// </remarks>
public sealed record LocalizedText(IReadOnlyDictionary<string, string> Values)
{
    public static LocalizedText Plain(string value) =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = value });

    /// <summary>Backs SIG0290. Every runtime fallback bottoms out at `en`.</summary>
    public bool HasEnglish => Values.Keys.Any(k => string.Equals(k, "en", StringComparison.OrdinalIgnoreCase));

    /// <summary>English text, for pack-time diagnostics and tests. Empty when absent.</summary>
    public string English =>
        Values.FirstOrDefault(kv => string.Equals(kv.Key, "en", StringComparison.OrdinalIgnoreCase)).Value
        ?? string.Empty;
}
