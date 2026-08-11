namespace SigilBuild.Wrapper.Tests.Helpers;

using System;
using System.Runtime.Versioning;
using System.Security.Principal;

/// <summary>
/// Whether this test process is running elevated, determined by a mechanism that is
/// deliberately <strong>not</strong> the one production code uses.
/// </summary>
/// <remarks>
/// <para>
/// This exists only as an <em>anti-vacuity guard</em> for tests whose correct expectation
/// differs between an elevated and an unelevated host. Those tests must branch on the
/// OUTCOME they observed, never on an elevation reading — a test shaped
/// <c>if (elevated) assertA(); else assertB();</c> derives its expectation from the same
/// signal the code under test consulted, so it agrees with the implementation by
/// construction and cannot fail. The correct shape is to branch on the observed outcome
/// and then assert, in the arm that could otherwise pass for the wrong reason, that the
/// host really is the one that outcome implies.
/// </para>
/// <para>
/// <c>Elevation.IsProcessElevated</c> asks the token directly
/// (<c>GetTokenInformation(TokenElevation)</c>). This asks a different question through a
/// different API — is the principal in the built-in Administrators role — so if the
/// production probe ever broke, the guard would disagree with the behaviour it is
/// guarding and the test would fail, which is exactly what a guard is for. Do not
/// "de-duplicate" this by calling the production helper.
/// </para>
/// </remarks>
internal static class ObservedElevation
{
    /// <summary>True when this process holds the built-in Administrators role.</summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        return IsElevatedWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevatedWindows()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
