namespace SigilBuild.Wrapper.Tests.Steps;

using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

[SupportedOSPlatform("windows")]
public class RegistryStepTests
{
    private const string Hkcu = "HKCU";

    [Fact]
    public async Task RegistryWrite_to_existing_key_records_prior_value_for_rollback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var k = TestRegistry.CreateScratchKey();
        k.SetValue("V", "before");

        var spec = new InstallStep.RegistryWrite(
            Id: "rw",
            Hive: Hkcu,
            Key: k.Path,
            Name: "V",
            Type: "REG_SZ",
            Value: "after",
            View: "native",
            When: null,
            OnFailure: OnFailure.Rollback);

        var step = new RegistryWriteStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        k.GetValue("V").Should().Be("after");

        await journal.UndoAsync(default);

        k.GetValue("V").Should().Be("before", "rollback must restore the prior value");
    }

    [Fact]
    public async Task RegistryWrite_to_absent_value_records_PreviouslyAbsent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var k = TestRegistry.CreateScratchKey();
        k.HasValue("V").Should().BeFalse();

        var spec = new InstallStep.RegistryWrite(
            Id: "rw",
            Hive: Hkcu,
            Key: k.Path,
            Name: "V",
            Type: "REG_SZ",
            Value: "new",
            View: "native",
            When: null,
            OnFailure: OnFailure.Rollback);

        var step = new RegistryWriteStep(spec);
        var journal = new RollbackJournal();

        await step.RunAsync(StepContext.Empty, journal, default);
        k.GetValue("V").Should().Be("new");

        await journal.UndoAsync(default);

        k.HasValue("V").Should().BeFalse("rollback must remove the value the step created");
    }

    [Theory]
    [InlineData("REG_SZ",        "hello",  RegistryValueKind.String)]
    [InlineData("REG_DWORD",     42,       RegistryValueKind.DWord)]
    [InlineData("REG_QWORD",     999_999L, RegistryValueKind.QWord)]
    [InlineData("REG_EXPAND_SZ", "%PATH%", RegistryValueKind.ExpandString)]
    public async Task RegistryWrite_round_trips_scalar_value_kinds(
        string typeStr, object raw, RegistryValueKind expectedKind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var k = TestRegistry.CreateScratchKey();

        var spec = new InstallStep.RegistryWrite(
            Id: "rw",
            Hive: Hkcu,
            Key: k.Path,
            Name: "V",
            Type: typeStr,
            Value: raw,
            View: "native",
            When: null,
            OnFailure: OnFailure.Fail);

        var step = new RegistryWriteStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        k.HasValue("V").Should().BeTrue();
        k.GetValueKind("V").Should().Be(expectedKind);
    }

    [Fact]
    public async Task RegistryWrite_round_trips_REG_MULTI_SZ()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var k = TestRegistry.CreateScratchKey();

        var spec = new InstallStep.RegistryWrite(
            Id: "rw",
            Hive: Hkcu,
            Key: k.Path,
            Name: "V",
            Type: "REG_MULTI_SZ",
            Value: new[] { "alpha", "beta", "gamma" },
            View: "native",
            When: null,
            OnFailure: OnFailure.Fail);

        var step = new RegistryWriteStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        k.GetValueKind("V").Should().Be(RegistryValueKind.MultiString);
        var stored = (string[])k.GetValue("V")!;
        stored.Should().BeEquivalentTo(new[] { "alpha", "beta", "gamma" });
    }

    [Fact]
    public async Task RegistryDeleteValue_rollback_restores_value()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var k = TestRegistry.CreateScratchKey();
        k.SetValue("V", "foo");

        var spec = new InstallStep.RegistryDeleteValue(
            Id: "rdv",
            Hive: Hkcu,
            Key: k.Path,
            Name: "V",
            View: "native",
            When: null,
            OnFailure: OnFailure.Rollback);

        var step = new RegistryDeleteValueStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        k.HasValue("V").Should().BeFalse();

        await journal.UndoAsync(default);

        k.GetValue("V").Should().Be("foo");
    }

    [Fact]
    public async Task RegistryDeleteValue_on_missing_value_is_noop_and_safe_to_undo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var k = TestRegistry.CreateScratchKey();

        var spec = new InstallStep.RegistryDeleteValue(
            Id: "rdv",
            Hive: Hkcu,
            Key: k.Path,
            Name: "MissingValue",
            View: "native",
            When: null,
            OnFailure: OnFailure.Rollback);

        var step = new RegistryDeleteValueStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        await journal.UndoAsync(default);
        k.HasValue("MissingValue").Should().BeFalse();
    }

    [Fact]
    public async Task RegistryDeleteKey_rollback_restores_immediate_values()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var k = TestRegistry.CreateScratchKey();
        k.SetValue("A", "alpha");
        k.SetValue("B", 7, RegistryValueKind.DWord);

        var spec = new InstallStep.RegistryDeleteKey(
            Id: "rdk",
            Hive: Hkcu,
            Key: k.Path,
            Recursive: true,
            View: "native",
            When: null,
            OnFailure: OnFailure.Rollback);

        var step = new RegistryDeleteKeyStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        k.Exists().Should().BeFalse("key should be gone after delete_key");

        await journal.UndoAsync(default);

        k.Exists().Should().BeTrue("rollback must re-create the key");
        k.GetValue("A").Should().Be("alpha");
        k.GetValue("B").Should().Be(7);
        k.GetValueKind("B").Should().Be(RegistryValueKind.DWord);
    }

    [Fact]
    public void ParseView_accepts_native_32_64_and_rejects_garbage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RegistryHelper.ParseView(null).Should().Be(RegistryView.Default);
        RegistryHelper.ParseView("").Should().Be(RegistryView.Default);
        RegistryHelper.ParseView("native").Should().Be(RegistryView.Default);
        RegistryHelper.ParseView("32").Should().Be(RegistryView.Registry32);
        RegistryHelper.ParseView("64").Should().Be(RegistryView.Registry64);

        FluentAssertions.AssertionExtensions
            .Should((Action)(() => RegistryHelper.ParseView("zzz")))
            .Throw<ArgumentException>();
    }
}
