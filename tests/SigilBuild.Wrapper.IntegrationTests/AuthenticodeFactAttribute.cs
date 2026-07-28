using System;
using System.IO;
using Xunit;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// Reports a genuine Skipped result when the real <c>WinVerifyTrust</c>
/// Authenticode preconditions — Windows only, no reference file needed — are
/// absent, instead of returning early and reporting as Passed (register row R6).
/// Used by <see cref="AuthenticodeVerifierTests.VerifyFile_returns_false_for_an_unsigned_binary"/>,
/// which fabricates its own unsigned fixture and needs nothing beyond Windows itself.
/// </summary>
internal sealed class AuthenticodeFactAttribute : FactAttribute
{
    public AuthenticodeFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Authenticode test: requires Windows";
        }
    }
}

/// <summary>
/// Reports a genuine Skipped result when either the base <see cref="AuthenticodeFactAttribute"/>
/// precondition (Windows) is unmet, or the stable Microsoft-signed reference file
/// (<c>%SystemRoot%\System32\kernel32.dll</c>) is absent — the extra precondition
/// <see cref="AuthenticodeVerifierTests.VerifyFile_returns_true_for_a_signed_system_binary"/>
/// needs (register row R6). A missing <c>kernel32.dll</c> on a live Windows host would
/// itself be extraordinary, but the original code guarded it defensively, so the
/// converted gate preserves that defense rather than assuming it can never happen.
/// </summary>
internal sealed class AuthenticodeReferenceFileFactAttribute : FactAttribute
{
    public AuthenticodeReferenceFileFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Authenticode test: requires Windows";
        }
        else if (!File.Exists(ReferenceFile))
        {
            Skip = "Authenticode test: reference signed system file not found at " + ReferenceFile;
        }
    }

    private static string ReferenceFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
}
