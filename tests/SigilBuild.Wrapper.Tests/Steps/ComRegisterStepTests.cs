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
/// (AGENTS.md §2).
/// <para>
/// Both failure modes also pin the <b>journal</b> contract: a register that
/// provably did not take effect leaves NO <c>UnregisterCom</c> record behind. That
/// is the R15 follow-up — see
/// <see cref="Step_maps_LoadFailed_to_a_failed_result_and_journals_nothing"/> for
/// why the pre-existing "still journals the inverse first" assertion became a
/// guaranteed uninstall failure.
/// </para>
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

    /// <summary>
    /// A load failure means the module never mapped, so <c>DllRegisterServer</c>
    /// never ran and nothing was registered — the journal must come out EMPTY.
    /// <para>
    /// This test previously asserted the opposite ("still journals the inverse
    /// first"), on the reasoning that journal-before-act is always safe because the
    /// undo tolerates a target that was never created. Since R15 that is false for
    /// this one record: <c>DllUnregisterServer</c> is the only probe a COM
    /// registration has, so a failure there is reported as "still registered" and
    /// fails the rollback and every subsequent uninstall attempt. A record kept here
    /// is a guaranteed <c>UndoFailedException</c> about a registration that never
    /// existed. The step now withdraws it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Step_maps_LoadFailed_to_a_failed_result_and_journals_nothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // R3/R9: com_register anchors its path to install_dir and requires an
        // admin-only-writable directory, so the arrangement uses System32 — a real
        // admin-only directory — with a file that does not exist in it. The DLL is
        // never loaded and nothing is ever registered.
        var dll = Path.Combine(Environment.SystemDirectory, "sigil-does-not-exist-nope.dll");
        var spec = new InstallStep.ComRegister("reg", dll, When: null, OnFailure: OnFailure.Continue);
        var journal = new RollbackJournal();

        var result = await new ComRegisterStep(spec)
            .RunAsync(SystemDirContext(), journal, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("LoadLibraryEx");
        journal.Records.Should().BeEmpty(
            "LoadLibraryEx failed, so DllRegisterServer never ran — an UnregisterCom " +
            "record here would fail every uninstall for a registration that was never made");
    }

    /// <summary>
    /// Same contract for the missing-export outcome — and the sharper case, since a
    /// DLL with no <c>DllRegisterServer</c> almost certainly has no
    /// <c>DllUnregisterServer</c> either, so the retained record's undo could not
    /// possibly succeed.
    /// </summary>
    [Fact]
    public async Task Step_maps_ExportMissing_to_a_helpful_failed_result_and_journals_nothing()
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
        journal.Records.Should().BeEmpty(
            "kernel32.dll exports neither DllRegisterServer nor DllUnregisterServer, so a " +
            "journaled UnregisterCom could only ever throw UndoFailedException");
    }

    /// <summary>
    /// The withdrawal must not swallow the record for an outcome that only proves the
    /// register <em>reported</em> failure: <c>DllRegisterServer</c> ran, and may have
    /// written part of its registration before giving up, so the undo has real work to
    /// attempt. Asserted at the journal level (an
    /// <c>HResultFailure</c> needs a real self-registering DLL that returns non-zero,
    /// which is the CI-VM fixture that does not exist yet) — <c>RetractLast</c> only
    /// removes the record it is handed, and only from the tail.
    /// </summary>
    [Fact]
    public void RetractLast_only_withdraws_the_tail_and_only_the_record_it_is_given()
    {
        var journal = new RollbackJournal();
        var undo = new RollbackRecord.UnregisterCom(@"C:\Program Files\Acme\Acme.Shell.dll");
        journal.Append(undo);

        journal.RetractLast(new RollbackRecord.UnregisterCom(@"C:\Program Files\Acme\Other.dll"))
            .Should().BeFalse("a step may only withdraw its own record");
        journal.Records.Should().ContainSingle();

        journal.Append(new RollbackRecord.RemoveDirectory(@"C:\Program Files\Acme"));
        journal.RetractLast(undo)
            .Should().BeFalse("the record is no longer the tail — a later step has journaled since");
        journal.Records.Should().HaveCount(2);

        var journal2 = new RollbackJournal();
        journal2.Append(undo);
        journal2.RetractLast(undo).Should().BeTrue();
        journal2.Records.Should().BeEmpty();
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
