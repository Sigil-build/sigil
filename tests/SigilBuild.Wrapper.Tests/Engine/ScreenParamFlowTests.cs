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
/// T9: wizard-collected parameter values must reach the engine under both the
/// <c>param.*</c> shorthand (used by the reference manifest) and the canonical
/// <c>parameters.*</c> namespace, and a step gated on <c>param.*</c> must honour
/// the collected value.
/// </summary>
public sealed class ScreenParamFlowTests
{
    private static WrapperBlob BlobWith(params ParameterDefinition[] parameters) =>
        new(
            AppId: "com.acme.Studio",
            Parameters: parameters,
            InstallSteps: Array.Empty<InstallStep>(),
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());

    private static ParameterDefinition Autostart() =>
        new("autostart", ParameterType.Bool, true, null, false, LocalizedText.Plain("Start when I sign in"), null, null, null);

    [Fact]
    public void Default_value_is_exposed_under_both_param_and_parameters_namespaces()
    {
        var blob = BlobWith(Autostart());
        var parsed = CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters);

        var ctx = StepContext.From(blob, parsed);

        ctx.Evaluate("param.autostart == true").Should().BeTrue();
        ctx.Evaluate("parameters.autostart == true").Should().BeTrue();
    }

    [Fact]
    public void Gui_collected_value_overrides_default_in_the_expression_context()
    {
        var blob = BlobWith(Autostart());
        var parsed = CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters);
        var collected = new Dictionary<string, string> { ["autostart"] = "false" };

        var ctx = StepContext.From(blob, parsed, payloadRoot: null, collected: collected);

        ctx.Evaluate("param.autostart == false").Should().BeTrue();
        ctx.Evaluate("parameters.autostart == false").Should().BeTrue();
    }

    [Fact]
    public async Task Step_gated_on_param_is_skipped_when_the_collected_value_is_false()
    {
        using var tmp = new TempDir();
        var gatedDir = Path.Combine(tmp.Path, "gated");
        var blob = BlobWith(Autostart());
        var parsed = CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters);

        var steps = new InstallStep[]
        {
            new InstallStep.DirectoryCreate("g", gatedDir, When: "param.autostart == true", OnFailure.Fail),
        };

        // autostart = false → the step's `when` is false → skipped.
        var ctxOff = StepContext.From(blob, parsed, null, new Dictionary<string, string> { ["autostart"] = "false" });
        var offResult = await new InstallEngine().RunAsync(steps, ctxOff, CancellationToken.None);
        offResult.Success.Should().BeTrue();
        Directory.Exists(gatedDir).Should().BeFalse("a step gated on param.autostart==true must not run when false");

        // autostart = true → the step runs.
        var ctxOn = StepContext.From(blob, parsed, null, new Dictionary<string, string> { ["autostart"] = "true" });
        var onResult = await new InstallEngine().RunAsync(steps, ctxOn, CancellationToken.None);
        onResult.Success.Should().BeTrue();
        Directory.Exists(gatedDir).Should().BeTrue("the gated step must run when param.autostart is true");
    }
}
