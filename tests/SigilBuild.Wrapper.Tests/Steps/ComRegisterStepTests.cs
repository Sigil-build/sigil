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
/// The <c>com_register</c> step and its native invocation primitive
/// (<see cref="ComRegistration"/>), exercised locally through two failure modes
/// needing neither admin nor a real self-registering DLL:
/// <see cref="ComRegistration.ComExportOutcome.LoadFailed"/> (non-existent path)
/// and <see cref="ComRegistration.ComExportOutcome.ExportMissing"/>
/// (<c>kernel32.dll</c>, a real system DLL with no <c>DllRegisterServer</c>
/// export). The live register→assert-HKCR-CLSID→unregister leg needs a real
/// self-registering COM DLL plus admin and is verified on the CI VM
/// (AGENTS.md §2).
/// <para>
/// Both failure modes also pin the <b>journal</b> contract in both directions: an
/// undo that cannot be called leaves NO <c>UnregisterCom</c> record behind (see
/// <see cref="Step_maps_LoadFailed_to_a_failed_result_and_journals_nothing"/>),
/// while a register that ran and failed KEEPS its record (see
/// <see cref="Step_keeps_the_journaled_undo_when_DllRegisterServer_ran_and_failed"/>,
/// which needs <see cref="ComRegistration.ComExportInvoker"/> since that outcome
/// is otherwise unreachable without the absent fixture DLL). (R15)
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
    /// A module that will not load will not load for the undo either, so the
    /// journaled <c>DllUnregisterServer</c> could never be called — the journal must
    /// come out EMPTY.
    /// <para>
    /// Do not journal the inverse before the undo is known callable: journal-before-act
    /// is not safe here, because <c>DllUnregisterServer</c> is the only probe a COM
    /// registration has, so a failure there is reported as "still registered" and
    /// fails the rollback and every subsequent uninstall attempt on a probe that never
    /// ran. The bar is the undo's feasibility, not proof that nothing was written —
    /// <c>LoadLibraryEx</c> reports failure when <c>DllMain</c> returns FALSE, after
    /// <c>DllMain</c> executed. (R15)
    /// </para>
    /// </summary>
    [Fact]
    public async Task Step_maps_LoadFailed_to_a_failed_result_and_journals_nothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // com_register anchors its path to install_dir and requires an
        // admin-only-writable directory, so the arrangement uses System32 — a real
        // admin-only directory — with a file that does not exist in it. The DLL is
        // never loaded and nothing is ever registered. (R3, R9)
        var dll = Path.Combine(Environment.SystemDirectory, "sigil-does-not-exist-nope.dll");
        var spec = new InstallStep.ComRegister("reg", dll, When: null, OnFailure: OnFailure.Continue);
        var journal = new RollbackJournal();

        var result = await new ComRegisterStep(spec)
            .RunAsync(SystemDirContext(), journal, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("LoadLibraryEx");
        journal.Records.Should().BeEmpty(
            "LoadLibraryEx failed, so the undo's DllUnregisterServer could not be called " +
            "either — an UnregisterCom record here would fail this rollback and every later " +
            "uninstall on the strength of a probe that never ran");
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
    /// The RETENTION half of the contract, and the one that needs a seam to bite.
    /// <c>HResultFailure</c> means <c>DllRegisterServer</c> ran and reported failure:
    /// it may have written part of its registration, and its
    /// <c>DllUnregisterServer</c> is callable — so the undo has real work to attempt
    /// and MUST survive.
    /// <para>
    /// Reaching that outcome for real needs a self-registering DLL returning non-zero
    /// (the CI-VM fixture that does not exist yet), so without
    /// <see cref="ComRegistration.ComExportInvoker"/> this half was unpinned: widening
    /// the step's withdrawal to <c>or HResultFailure</c> — which would silently discard
    /// the undo for a registration that partly happened — passed the entire suite.
    /// With the stub it fails here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Step_keeps_the_journaled_undo_when_DllRegisterServer_ran_and_failed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // E_FAIL from a DllRegisterServer that executed. Nothing is loaded or
        // registered: the native invocation itself is stubbed out.
        var dll = Path.Combine(Environment.SystemDirectory, "sigil-partial-registrar.dll");
        var spec = new InstallStep.ComRegister("reg", dll, When: null, OnFailure: OnFailure.Continue);
        var journal = new RollbackJournal();
        var invoked = 0;

        var result = await new ComRegisterStep(spec, (path, export) =>
        {
            invoked++;
            path.Should().Be(dll);
            export.Should().Be("DllRegisterServer");
            return new ComRegistration.ComInvocationResult(
                ComRegistration.ComExportOutcome.HResultFailure, Win32Error: 0, HResult: unchecked((int)0x80004005));
        }).RunAsync(SystemDirContext(), journal, default);

        invoked.Should().Be(1, "the step must go through the seam, not the real LoadLibraryEx");
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("0x80004005");
        journal.Records.Should().ContainSingle(
            "DllRegisterServer ran and may have written part of its registration before " +
            "failing, and its DllUnregisterServer is callable — so the undo must survive")
            .Which.Should().BeOfType<RollbackRecord.UnregisterCom>()
            .Which.DllPath.Should().Be(dll);
    }

    /// <summary>
    /// A successful register obviously keeps its undo — the same seam, the same
    /// assertion, and the outcome the fixture DLL will eventually exercise for real.
    /// </summary>
    [Fact]
    public async Task Step_keeps_the_journaled_undo_when_the_register_succeeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dll = Path.Combine(Environment.SystemDirectory, "sigil-good-registrar.dll");
        var spec = new InstallStep.ComRegister("reg", dll, When: null, OnFailure: OnFailure.Continue);
        var journal = new RollbackJournal();

        var result = await new ComRegisterStep(spec, (_, _) => new ComRegistration.ComInvocationResult(
                ComRegistration.ComExportOutcome.Ok, Win32Error: 0, HResult: 0))
            .RunAsync(SystemDirContext(), journal, default);

        result.Success.Should().BeTrue(result.Error);
        journal.Records.Should().ContainSingle()
            .Which.Should().BeOfType<RollbackRecord.UnregisterCom>()
            .Which.DllPath.Should().Be(dll);
    }

    /// <summary>
    /// <c>RetractLast</c>'s own contract: it removes only the record it is handed, and
    /// only while that record is still the tail.
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
    /// admin-only-writable directory — so the privileged-target guard
    /// admits the arrangement and the step reaches the outcome under test.
    /// Refusal cases live in <c>PrivilegedStepContainmentTests</c>. (R3, R9)
    /// </summary>
    private static StepContext SystemDirContext() =>
        new(new System.Collections.Generic.Dictionary<string, object?>(),
            scope: InstallScope.Machine,
            installDir: Environment.SystemDirectory,
            appId: "com.example.myapp");
}
