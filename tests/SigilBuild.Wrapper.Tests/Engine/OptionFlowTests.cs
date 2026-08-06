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

    // ── P10 (gap G11): app-defined custom components ─────────────────────────

    private static InstallerOptionComponent Custom(
        string name, bool @default = false, bool locked = false, string? when = null) =>
        new(name, @default, locked, Custom: true, Label: LocalizedText.Plain(name), When: when);

    [Fact]
    public void Custom_component_default_is_exposed_under_the_option_namespace()
    {
        var blob = BlobWith(Custom("sample_data", @default: true));
        var ctx = StepContext.From(blob, NoArgs(blob));

        ctx.Evaluate("option.sample_data").Should().BeTrue();
    }

    [Fact]
    public void Custom_component_cli_override_is_namespaced_under_option()
    {
        var blob = BlobWith(Custom("sample_data", @default: true));
        // The CLI stores a custom override under the namespaced `option.<name>` key
        // (avoids colliding with a same-named parameter). StepContext must look it up there.
        var withOverride = new ParsedCommandLine
        {
            Options = new Dictionary<string, string> { ["option.sample_data"] = "false" },
        };

        var ctx = StepContext.From(blob, withOverride);

        ctx.Evaluate("option.sample_data == false").Should().BeTrue("the namespaced /Poption. override wins over the default");
    }

    [Fact]
    public void Custom_component_when_false_forces_the_option_off()
    {
        var blob = new WrapperBlob(
            AppId: "com.acme.Studio",
            Parameters: new[]
            {
                new ParameterDefinition("edition", ParameterType.String, "std",
                    EnumValues: null, InstallTime: true, Description: null, Pattern: null, Min: null, Max: null),
            },
            InstallSteps: Array.Empty<InstallStep>(),
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Options: new[] { Custom("pro_stuff", @default: true, when: "param.edition == 'pro'") });

        // edition defaults to 'std' → the component is not applicable → option is off,
        // regardless of its default: true.
        var ctxStd = StepContext.From(blob, NoArgs(blob));
        ctxStd.Evaluate("option.pro_stuff == false").Should().BeTrue("an inapplicable component resolves off");

        // edition = pro → applicable → resolves to its default (true).
        var parsedPro = CommandLineParser.Parse(new[] { "/Pedition=pro" }, blob.Parameters);
        var ctxPro = StepContext.From(blob, parsedPro);
        ctxPro.Evaluate("option.pro_stuff").Should().BeTrue("an applicable component resolves to its default");
    }

    [Fact]
    public async Task Custom_components_gate_file_copy_groups_end_to_end()
    {
        using var tmp = new TempDir();
        // Two payload source groups, one per custom component.
        var srcA = Path.Combine(tmp.Path, "srcA");
        var srcB = Path.Combine(tmp.Path, "srcB");
        Directory.CreateDirectory(srcA);
        Directory.CreateDirectory(srcB);
        File.WriteAllText(Path.Combine(srcA, "a.txt"), "A");
        File.WriteAllText(Path.Combine(srcB, "b.txt"), "B");

        var dstA = Path.Combine(tmp.Path, "install", "A");
        var dstB = Path.Combine(tmp.Path, "install", "B");

        var blob = new WrapperBlob(
            AppId: "com.acme.Studio",
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: new InstallStep[]
            {
                // R16 contains file_copy's `to` to install_dir; these two copy
                // into an OS temp directory, so the fixture declares the
                // out-of-tree write with the production per-step opt-out rather
                // than relaxing the rule. What is under test here is option
                // gating, not containment.
                new InstallStep.FileCopy("cpA", Path.Combine(srcA, "*"), dstA,
                    Overwrite: true, When: "option.feature_a", OnFailure.Fail)
                    { AllowOutsideInstallDir = true },
                new InstallStep.FileCopy("cpB", Path.Combine(srcB, "*"), dstB,
                    Overwrite: true, When: "option.feature_b", OnFailure.Fail)
                    { AllowOutsideInstallDir = true },
            },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Options: new[]
            {
                Custom("feature_a", @default: true),
                Custom("feature_b", @default: false),
            });
        var steps = new List<InstallStep>(blob.InstallSteps).ToArray();

        // Silent defaults: feature_a on → A copied; feature_b off → B skipped.
        var ctxDefault = StepContext.From(blob, NoArgs(blob));
        (await new InstallEngine().RunAsync(steps, ctxDefault, CancellationToken.None)).Success.Should().BeTrue();
        File.Exists(Path.Combine(dstA, "a.txt")).Should().BeTrue("feature_a defaults on");
        File.Exists(Path.Combine(dstB, "b.txt")).Should().BeFalse("feature_b defaults off");

        // Namespaced CLI overrides flip both: A skipped, B copied.
        Directory.Delete(Path.Combine(tmp.Path, "install"), recursive: true);
        var flipped = new ParsedCommandLine
        {
            Options = new Dictionary<string, string>
            {
                ["option.feature_a"] = "false",
                ["option.feature_b"] = "true",
            },
        };
        var ctxFlipped = StepContext.From(blob, flipped);
        (await new InstallEngine().RunAsync(steps, ctxFlipped, CancellationToken.None)).Success.Should().BeTrue();
        File.Exists(Path.Combine(dstA, "a.txt")).Should().BeFalse("/Poption.feature_a=false skips its group");
        File.Exists(Path.Combine(dstB, "b.txt")).Should().BeTrue("/Poption.feature_b=true runs its group");
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
