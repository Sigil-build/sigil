using System;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// A <see cref="FactAttribute"/> that reports a genuine Skipped result on non-Windows
/// hosts instead of passing vacuously. Register row R6: the repo's older
/// <c>if (!OperatingSystem.IsWindows()) return;</c> pattern reports as PASSED, which is
/// the defect this track exists to fix. Mirrors
/// <c>SigilBuild.Wrapper.Tests.Helpers.WindowsFactAttribute</c>; the two assemblies
/// share no test-helper project, and duplicating six lines beats introducing one.
/// </summary>
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
