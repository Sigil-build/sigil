using System;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// A <see cref="FactAttribute"/> that reports a genuine Skipped result on non-Windows
/// hosts instead of passing vacuously — an
/// <c>if (!OperatingSystem.IsWindows()) return;</c> pattern reports as PASSED instead.
/// Mirrors <c>SigilBuild.Wrapper.Tests.Helpers.WindowsFactAttribute</c>; the two
/// assemblies share no test-helper project, and duplicating six lines beats
/// introducing one. (R6)
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
