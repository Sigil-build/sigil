namespace SigilBuild.Wrapper.Tests.Steps;

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using Xunit;

[SupportedOSPlatform("windows")]
public class EnvSetStepTests
{
    [Theory]
    // Explicit user/machine is authoritative regardless of install scope.
    [InlineData("user", InstallScope.Machine, "user")]
    [InlineData("machine", InstallScope.User, "machine")]
    // "auto" (and empty) defer to the resolved install scope (T12).
    [InlineData("auto", InstallScope.Machine, "machine")]
    [InlineData("auto", InstallScope.User, "user")]
    [InlineData("", InstallScope.Machine, "machine")]
    public void ResolveEnvScope_defers_auto_to_the_install_scope(
        string specScope, InstallScope installScope, string expected)
    {
        var ctx = new StepContext(new Dictionary<string, object?>(), scope: installScope);
        EnvSetStep.ResolveEnvScope(specScope, ctx).Should().Be(expected);
    }

    [Theory]
    [InlineData("set", "old", "new", ";", "new")]
    [InlineData("set", null, "new", ";", "new")]
    [InlineData("append", "C:/a;C:/b", "NEW", ";", "C:/a;C:/b;NEW")]
    [InlineData("prepend", "C:/a;C:/b", "NEW", ";", "NEW;C:/a;C:/b")]
    [InlineData("append", null, "NEW", ";", "NEW")]
    [InlineData("append", "", "NEW", ";", "NEW")]
    [InlineData("prepend", null, "NEW", ";", "NEW")]
    [InlineData("prepend", "", "NEW", ";", "NEW")]
    public void ComputeNewValue_returns_correct_combined_string(
        string action, string? prior, string value, string sep, string expected)
    {
        EnvSetStep.ComputeNewValue(action, prior, value, sep).Should().Be(expected);
    }

    [Fact]
    public void ComputeNewValue_throws_on_unknown_action()
    {
        FluentAssertions.AssertionExtensions
            .Should((Action)(() => EnvSetStep.ComputeNewValue("nope", null, "x", ";")))
            .Throw<ArgumentException>();
    }

    [Fact]
    public async Task Set_action_round_trips_via_HKCU_Environment_with_rollback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = "SIGIL_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            var spec = new InstallStep.EnvSet(
                Id: "s",
                Name: name,
                Value: "hello",
                Scope: "user",
                Action: "set",
                Separator: ";",
                When: null,
                OnFailure: OnFailure.Fail);

            var journal = new RollbackJournal();
            var result = await new EnvSetStep(spec).RunAsync(StepContext.Empty, journal, default);

            result.Success.Should().BeTrue();
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                .Should().Be("hello");

            await journal.UndoAsync(default);

            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                .Should().BeNull("rollback should remove a previously-absent variable");
        }
        finally
        {
            // Best-effort cleanup in case the test threw mid-flight.
#pragma warning disable CA1031
            try { Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User); }
            catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }

    [Fact]
    public async Task Append_action_preserves_prior_value_and_rollback_restores_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = "SIGIL_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            // Seed a prior value so we exercise the append + restore path.
            Environment.SetEnvironmentVariable(name, "C:/prior", EnvironmentVariableTarget.User);

            var spec = new InstallStep.EnvSet(
                Id: "s",
                Name: name,
                Value: "C:/added",
                Scope: "user",
                Action: "append",
                Separator: ";",
                When: null,
                OnFailure: OnFailure.Fail);

            var journal = new RollbackJournal();
            var result = await new EnvSetStep(spec).RunAsync(StepContext.Empty, journal, default);

            result.Success.Should().BeTrue();
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                .Should().Be("C:/prior;C:/added");

            await journal.UndoAsync(default);

            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                .Should().Be("C:/prior", "rollback must restore the prior value verbatim");
        }
        finally
        {
#pragma warning disable CA1031
            try { Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User); }
            catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }
}
