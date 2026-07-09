using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// T8: the ENABLED built-in option components are exposed to the expression engine
/// as <c>option.*</c> so an auto-generated (or hand-written) step gated on
/// <c>option.&lt;component&gt;</c> honours the resolved value. Resolution precedence:
/// a <c>locked</c> component is fixed at its default; otherwise
/// wizard-collected → CLI <c>/P&lt;name&gt;</c> → manifest default.
/// </summary>
public sealed class OptionFlowTests
{
    private static WrapperBlob BlobWith(params InstallerOptionComponent[] options) =>
        new(
            AppId: "com.acme.Studio",
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: Array.Empty<InstallStep>(),
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Options: options);

    private static ParsedCommandLine NoArgs(WrapperBlob blob) =>
        CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters);

    [Fact]
    public void Default_is_exposed_under_the_option_namespace()
    {
        var blob = BlobWith(new InstallerOptionComponent("desktop_shortcut", Default: true, Locked: false));
        var ctx = StepContext.From(blob, NoArgs(blob));

        ctx.Evaluate("option.desktop_shortcut").Should().BeTrue("a bare option identifier evaluates as its bool");
        ctx.Evaluate("option.desktop_shortcut == true").Should().BeTrue();
    }

    [Fact]
    public void Collected_checkbox_value_overrides_the_default()
    {
        var blob = BlobWith(new InstallerOptionComponent("desktop_shortcut", Default: true, Locked: false));
        var collected = new Dictionary<string, bool> { ["desktop_shortcut"] = false };

        var ctx = StepContext.From(blob, NoArgs(blob), payloadRoot: null, collected: null,
            scope: InstallScope.User, collectedOptions: collected);

        ctx.Evaluate("option.desktop_shortcut == false").Should().BeTrue();
    }

    [Fact]
    public void Cli_override_applies_when_no_collected_value()
    {
        var blob = BlobWith(new InstallerOptionComponent("add_to_path", Default: true, Locked: false));
        var parsed = CommandLineParser.Parse(new[] { "/Padd_to_path=false" }, blob.Parameters);

        var ctx = StepContext.From(blob, parsed);

        ctx.Evaluate("option.add_to_path == false").Should().BeTrue("the CLI /P override wins over the default");
    }

    [Fact]
    public void Locked_component_ignores_the_collected_value()
    {
        var blob = BlobWith(new InstallerOptionComponent("add_to_path", Default: true, Locked: true));
        var collected = new Dictionary<string, bool> { ["add_to_path"] = false };

        var ctx = StepContext.From(blob, NoArgs(blob), payloadRoot: null, collected: null,
            scope: InstallScope.User, collectedOptions: collected);

        ctx.Evaluate("option.add_to_path == true").Should()
            .BeTrue("a locked component stays fixed at its default regardless of the checkbox");
    }

    [Fact]
    public void Option_is_usable_in_a_hand_written_step_when()
    {
        var blob = BlobWith(new InstallerOptionComponent("desktop_shortcut", Default: false, Locked: false));
        var ctx = StepContext.From(blob, NoArgs(blob));

        // A hand-authored expression referencing option.* resolves like any other.
        ctx.Evaluate("!option.desktop_shortcut").Should().BeTrue();
        ctx.Evaluate("option.desktop_shortcut || true").Should().BeTrue();
    }

    [Fact]
    public async Task Step_gated_on_option_is_skipped_when_false_and_runs_when_true()
    {
        using var tmp = new TempDir();
        var gatedDir = Path.Combine(tmp.Path, "gated");
        var blob = BlobWith(new InstallerOptionComponent("desktop_shortcut", Default: true, Locked: false));
        var parsed = NoArgs(blob);

        var steps = new InstallStep[]
        {
            new InstallStep.DirectoryCreate("g", gatedDir, When: "option.desktop_shortcut", OnFailure.Fail),
        };

        // desktop_shortcut = false (unchecked) → the step's `when` is false → skipped.
        var ctxOff = StepContext.From(blob, parsed, null, null, InstallScope.User,
            new Dictionary<string, bool> { ["desktop_shortcut"] = false });
        var offResult = await new InstallEngine().RunAsync(steps, ctxOff, CancellationToken.None);
        offResult.Success.Should().BeTrue();
        Directory.Exists(gatedDir).Should().BeFalse("toggling desktop_shortcut off skips its generated step");

        // desktop_shortcut = true (checked) → the step runs.
        var ctxOn = StepContext.From(blob, parsed, null, null, InstallScope.User,
            new Dictionary<string, bool> { ["desktop_shortcut"] = true });
        var onResult = await new InstallEngine().RunAsync(steps, ctxOn, CancellationToken.None);
        onResult.Success.Should().BeTrue();
        Directory.Exists(gatedDir).Should().BeTrue("the option-gated step runs when the box is checked");
    }
}
