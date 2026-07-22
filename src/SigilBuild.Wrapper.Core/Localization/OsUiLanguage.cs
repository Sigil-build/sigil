using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Wrapper.Core.Localization;

/// <summary>
/// Reads the user's ordered UI-language preferences from Win32.
/// </summary>
/// <remarks>
/// CultureInfo cannot be used: InvariantGlobalization=true makes
/// CurrentUICulture.Name always "" and makes `new CultureInfo("uk-UA")` throw.
/// GetUserPreferredUILanguages is a bounded, read-only, deterministic probe, so
/// locale() stays inside ADR-008 §1.2 — only its source changes, not its
/// contract. Every failure path returns empty, keeping the function total.
/// </remarks>
public static partial class OsUiLanguage   // partial is required by [LibraryImport]
{
    private const uint MuiLanguageName = 0x8;

    [LibraryImport("kernel32.dll", EntryPoint = "GetUserPreferredUILanguages", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserPreferredUILanguages(
        uint dwFlags, out uint pulNumLanguages, Span<char> pwszLanguagesBuffer, ref uint pcchLanguagesBuffer);

    public static IReadOnlyList<string> Preferences()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Array.Empty<string>();
        }

        try
        {
            uint count = 0;
            uint chars = 0;

            // First call sizes the buffer.
            if (!GetUserPreferredUILanguages(MuiLanguageName, out count, Span<char>.Empty, ref chars) || chars == 0)
            {
                return Array.Empty<string>();
            }

            var buffer = new char[chars];
            if (!GetUserPreferredUILanguages(MuiLanguageName, out count, buffer, ref chars))
            {
                return Array.Empty<string>();
            }

            // Double-null-terminated, null-separated list.
            var result = new List<string>((int)count);
            var start = 0;
            for (var i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != '\0')
                {
                    continue;
                }

                if (i > start)
                {
                    var tag = new string(buffer, start, i - start);
                    if (LanguageTag.IsValid(tag))
                    {
                        result.Add(tag);
                    }
                }

                start = i + 1;
                if (start < buffer.Length && buffer[start] == '\0')
                {
                    break; // double null: end of list
                }
            }

            return result;
        }
        catch (Exception)
        {
            // Total by contract (ADR-008 §1.2): an absent/denied path yields "".
            return Array.Empty<string>();
        }
    }

    public static string Primary()
    {
        var prefs = Preferences();
        return prefs.Count > 0 ? prefs[0] : string.Empty;
    }
}
