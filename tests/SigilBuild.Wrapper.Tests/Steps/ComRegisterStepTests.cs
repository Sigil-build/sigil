namespace SigilBuild.Wrapper.Tests.Steps;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Steps.Win32;
using Xunit;

/// <summary>
/// T11.2 (P11): the <c>com_register</c> step and its native invocation
/// primitive (<see cref="ComRegistration"/>). The AOT-safe unmanaged
/// function-pointer path is exercised through two failure modes that need
/// neither admin nor a real self-registering DLL, and are therefore runnable
/// locally on Windows:
/// <list type="bullet">
/// <item>a non-existent path → <see cref="ComRegistration.ComExportOutcome.LoadFailed"/>;</item>
/// <item>a real system DLL with no <c>DllRegisterServer</c> export
/// (<c>kernel32.dll</c>) → <see cref="ComRegistration.ComExportOutcome.ExportMissing"/>.</item>
/// </list>
/// The live register→assert-HKCR-CLSID→unregister leg needs a real
/// self-registering COM DLL plus admin and is verified on the CI VM
/// (AGENTS.md §2). The rollback ORDERING (journal the inverse BEFORE the native
/// call) is asserted here locally.
/// </summary>
[SupportedOSPlatform("windows")]
public class ComRegisterStepTests
{
    [Fact]
    public void Invoke_returns_LoadFailed_for_a_nonexistent_dll()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = ComRegistration.Invoke(@"C:\does\not\exist\nope.dll", "DllRegisterServer");

        result.Outcome.Should().Be(ComRegistration.ComExportOutcome.LoadFailed);
        result.Win32Error.Should().NotBe(0, "LoadLibraryEx sets a Win32 error on failure");
    }

    [Fact]
    public void Invoke_returns_ExportMissing_for_a_dll_without_the_export()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // kernel32.dll loads fine but is not a self-registering COM DLL — it has
        // no DllRegisterServer export, so GetProcAddress returns NULL.
        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");

        var result = ComRegistration.Invoke(kernel32, "DllRegisterServer");

        result.Outcome.Should().Be(ComRegistration.ComExportOutcome.ExportMissing);
    }

    [Fact]
    public async Task Step_maps_LoadFailed_to_a_failed_result_and_still_journals_the_inverse_first()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The rollback record is appended BEFORE the native call, so even when
        // the register itself fails (here: the DLL can't load) the journal
        // already knows how to undo. Path only — no secrets.
        //
        // R3/R9: com_register now anchors its path to install_dir and requires an
        // admin-only-writable directory, so the arrangement uses System32 — a real
        // admin-only directory — with a file that does not exist in it. The DLL is
        // still never loaded and nothing is ever registered.
        var dll = Path.Combine(Environment.SystemDirectory, "sigil-does-not-exist-nope.dll");
        var spec = new InstallStep.ComRegister("reg", dll, When: null, OnFailure: OnFailure.Continue);
        var journal = new RollbackJournal();

        var result = await new ComRegisterStep(spec)
            .RunAsync(SystemDirContext(), journal, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("LoadLibraryEx");
        journal.Records.Should().ContainSingle()
            .Which.Should().BeOfType<RollbackRecord.UnregisterCom>()
            .Which.DllPath.Should().Be(dll);
    }

    [Fact]
    public async Task Step_maps_ExportMissing_to_a_helpful_failed_result()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var spec = new InstallStep.ComRegister("reg", kernel32, When: null, OnFailure: OnFailure.Continue);
        var journal = new RollbackJournal();

        var result = await new ComRegisterStep(spec).RunAsync(SystemDirContext(), journal, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("self-registering COM DLL");
        journal.Records.Should().ContainSingle()
            .Which.Should().BeOfType<RollbackRecord.UnregisterCom>();
    }

    /// <summary>
    /// A context anchored on <c>%WINDIR%\System32</c> — an existing, real
    /// admin-only-writable directory — so the R3/R9 privileged-target guard
    /// admits the arrangement and the step reaches the outcome under test.
    /// Refusal cases live in <c>PrivilegedStepContainmentTests</c>.
    /// </summary>
    private static StepContext SystemDirContext() =>
        new(new System.Collections.Generic.Dictionary<string, object?>(),
            scope: InstallScope.Machine,
            installDir: Environment.SystemDirectory,
            appId: "com.example.myapp");
}
