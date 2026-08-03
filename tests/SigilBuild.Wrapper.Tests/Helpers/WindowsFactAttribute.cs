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
