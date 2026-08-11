namespace SigilBuild.Wrapper.Tests.Helpers;

using System;
using Xunit;

/// <summary>
/// A <see cref="FactAttribute"/> that reports a genuine Skipped result on
/// non-Windows hosts instead of passing vacuously. See register row R6: the
/// repo's existing <c>if (!OperatingSystem.IsWindows()) return;</c> pattern reports
/// as PASSED, which is the defect this track exists to fix.
/// </summary>
/// <remarks>
/// <c>tests/SigilBuild.Wrapper.Tests</c> is on xunit 2.9.2, whose
/// <c>Xunit.Assert</c> has no <c>Skip</c>/<c>SkipUnless</c> methods; setting the
/// inherited <see cref="FactAttribute.Skip"/> property in the constructor is the
/// supported v2 pattern and produces a real Skipped result in the trx.
/// </remarks>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string reason = "Windows-only API")
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for the narrow case of a Windows test whose
/// assertion is only observable when the current process is <b>not</b> elevated —
/// today just the run-after-install de-elevation side effect (see
/// <c>LaunchTests</c>). Reports a genuine Skipped result on a non-Windows host or
/// an elevated one, rather than the <c>if (Elevation.IsProcessElevated()) return;</c>
/// early return that reported PASSED on every elevated runner (register row R6).
/// </summary>
/// <remarks>
/// CI runs elevated, so this gate fires there: the skip reason in the trx is the
/// honest statement that the de-elevation path's observable effect belongs to the
/// VM matrix, not to a unit-test host.
/// </remarks>
internal sealed class UnelevatedWindowsFactAttribute : FactAttribute
{
    public UnelevatedWindowsFactAttribute(string reason = "Windows-only API")
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = reason;
        }
        else if (SigilBuild.Wrapper.Engine.Elevation.IsProcessElevated())
        {
            Skip = "process is elevated: the de-elevation launch path's side effect is not "
                 + "observable from an elevated host (belongs to the VM matrix)";
        }
    }
}

/// <summary>
/// The <see cref="TheoryAttribute"/> counterpart of <see cref="WindowsFactAttribute"/>.
/// Same contract, same reason: a genuine Skipped result off Windows.
/// </summary>
internal sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute(string reason = "Windows-only API")
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = reason;
        }
    }
}
