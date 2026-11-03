// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Wrapper.Engine;

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

/// <summary>
/// Detects over-the-shoulder elevation: an elevated process whose token belongs to a
/// different account than the one signed in to the desktop session — a standard user
/// who typed an administrator's credentials into the UAC prompt. In that run "Just
/// for me" means the administrator's profile, not the user's. (R118)
/// </summary>
/// <remarks>
/// <para>
/// The desktop user is read with <c>WTSQuerySessionInformationW</c>
/// (<c>WTSUserName</c> + <c>WTSDomainName</c>) for this process's own session, and
/// compared by SID with this process's token user. Both reads work from an ordinary
/// elevated process. <c>WTSQueryUserToken</c> would answer the same question but
/// requires SYSTEM's <c>SeTcbPrivilege</c>, which an installer never holds. Passing
/// the unelevated parent's SID through the relaunch would only cover runs this
/// installer relaunched itself, not a "Run as administrator" started from Explorer,
/// and it would be a command-line claim rather than something the OS reports.
/// </para>
/// <para>
/// An unknown answer is "same user": a session with no signed-in user (a deployment
/// tool running as SYSTEM in session 0) or a failed query keeps today's behaviour
/// rather than taking options away from a run that has no desktop user to protect.
/// </para>
/// All interop is source-generated <c>[LibraryImport]</c> (Native-AOT safe).
/// </remarks>
public static partial class ElevationIdentity
{
    /// <summary>
    /// True when this process is elevated and its token user differs from the user
    /// signed in to its desktop session. Always false off Windows, unelevated, or when
    /// either identity cannot be established.
    /// </summary>
    public static bool IsElevatedAsAnotherUser()
    {
        if (!OperatingSystem.IsWindows() || !Elevation.IsProcessElevated())
        {
            return false;
        }
        return IsElevatedAsAnotherUserWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevatedAsAnotherUserWindows()
    {
        var sessionUser = TryGetSessionUserSid();
        if (sessionUser is null)
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is { } tokenUser && !tokenUser.Equals(sessionUser);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? TryGetSessionUserSid()
    {
        var user = QuerySessionString(WTSUserName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }
        var domain = QuerySessionString(WTSDomainName);

#pragma warning disable CA1031 // An unmappable account is "unknown", never a crash.
        try
        {
            var account = string.IsNullOrEmpty(domain) ? new NTAccount(user) : new NTAccount(domain, user);
            return account.Translate(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    [SupportedOSPlatform("windows")]
    private static string? QuerySessionString(int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WTS_CURRENT_SESSION, infoClass, out var buffer, out _))
        {
            return null;
        }
        try
        {
            return buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    private const uint WTS_CURRENT_SESSION = 0xFFFFFFFF;
    private const int WTSUserName = 5;
    private const int WTSDomainName = 7;

    [SupportedOSPlatform("windows")]
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(
        IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [SupportedOSPlatform("windows")]
    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(IntPtr memory);
}
