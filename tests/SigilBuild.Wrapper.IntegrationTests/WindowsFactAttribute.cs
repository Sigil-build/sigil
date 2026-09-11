using System;
using Xunit;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// Reports a genuine Skipped result on a non-Windows host instead of passing
/// vacuously (R6), with <b>no</b> VM opt-in and <b>no</b> elevation requirement —
/// for the assertions in this assembly that are pure Windows-API computation and
/// therefore belong in the ordinary PR run rather than the VM matrix. Used by
/// <see cref="SystemStepAnchorTests"/>.
/// </summary>
/// <remarks>
/// The counterpart of <c>SigilBuild.Wrapper.Tests.Helpers.WindowsFactAttribute</c>,
/// which this assembly cannot reference. Deliberately distinct from
/// <see cref="VmFactAttribute"/>: a test gated on <c>SIGIL_VM_TESTS</c> is proved
/// only when the VM matrix runs, which is exactly the property that let
/// system-step legs go a full release cycle without their first real execution.
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
