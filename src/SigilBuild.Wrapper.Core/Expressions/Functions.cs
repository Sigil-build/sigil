using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace SigilBuild.Wrapper.Expressions;

/// <summary>
/// Closed function table. Anything not present here throws
/// <see cref="ExpressionException"/> at evaluation time.
///
/// SECURITY: do NOT add functions that shell out, do reflection, or
/// perform I/O outside the bounded read-only allowance (file/registry/env
/// reads). Anyone proposing such a feature must amend ADR-008 first —
/// see <c>docs/architecture/adr-008-expression-policy.md</c> §1 (closed
/// function catalog + admission criteria).
/// </summary>
internal static class Functions
{
    public static readonly IReadOnlyDictionary<string, Func<object?[], object?>> Table
        = new Dictionary<string, Func<object?[], object?>>(StringComparer.Ordinal)
        {
            // defined / empty get special handling in Evaluator.CallFunction —
            // they need to observe missing identifiers as "absent" rather than
            // a hard parse error. By the time this lambda runs, the argument
            // has already been resolved (or replaced with null on the
            // unknown-identifier path).
            ["defined"] = a => a[0] is not null,

            ["empty"] = a => a[0] is null
                || (a[0] is string s && s.Length == 0)
                || (a[0] is ICollection col && col.Count == 0),

            ["version_gte"] = a => CompareVersion(ToStringOrNull(a[0]), ToStringOrNull(a[1])) >= 0,

            ["os_version"] = _ => Environment.OSVersion.Version.ToString(),

            ["arch"] = _ => RuntimeInformation.ProcessArchitecture
                .ToString().ToLowerInvariant(),

            // CurrentUICulture.Name is "" under InvariantGlobalization=true
            // but the function is still callable; tests assert non-empty for
            // os_version() and arch() only.
            ["locale"] = _ => CultureInfo.CurrentUICulture.Name,

            ["file_exists"] = a => File.Exists(ToStringOrNull(a[0])),

            ["registry_exists"] = a => RegistryExists(
                ToStringOrNull(a[0]),
                ToStringOrNull(a[1]),
                ToStringOrNull(a[2])),
        };

    // Indirection so the analyzer sees an OS guard for the Windows-only
    // `Engine.RegistryHelper.Exists`. The wrapper itself only ships on
    // Windows (RID=win-x64), but Functions.cs is platform-agnostic.
    private static bool RegistryExists(string? hive, string? key, string? name)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return SigilBuild.Wrapper.Engine.RegistryHelper.Exists(hive, key, name);
    }

    private static string? ToStringOrNull(object? value) =>
        value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    private static int CompareVersion(string? a, string? b)
    {
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
        {
            return va.CompareTo(vb);
        }

        return string.CompareOrdinal(a, b);
    }
}
