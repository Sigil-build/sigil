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
/// The <c>{install_dir}</c> contract (T13): the token resolves in step paths and
/// <c>when</c> expressions; <c>/D=</c> relocates the install; and the concrete
/// regression — a <c>file_copy to "{install_dir}"</c> under <c>/silent /D=&lt;tmp&gt;</c>
/// lands the file under <c>&lt;tmp&gt;</c>, not in a literal <c>{install_dir}</c> folder.
/// </summary>
public sealed class InstallDirContractTests
{
    // --- Token substitution in step paths (StepContext.Resolve) ---

    [Fact]
    public void Resolve_substitutes_install_dir_token_in_a_step_path()
    {
        var root = Path.Combine("C:", "Apps", "Acme");
        var ctx = new StepContext(new Dictionary<string, object?>(), installDir: root);

        ctx.Resolve("{install_dir}/app.txt").Should().Be(root + "/app.txt");
        ctx.InstallDir.Should().Be(root);
    }

    [Fact]
    public void Resolve_substitutes_scope_root_and_app_tokens()
    {
        var ctx = new StepContext(
            new Dictionary<string, object?>(),
            scope: InstallScope.Machine,
            appName: "Acme Studio",
            appId: "com.acme.Studio");

        ctx.Resolve("{scope_root}").Should().Be(ScopeLayout.For(InstallScope.Machine).InstallRoot);
        ctx.Resolve("{app.name}").Should().Be("Acme Studio");
        ctx.Resolve("{app.id}").Should().Be("com.acme.Studio");
    }

    [Fact]
    public void Resolve_leaves_install_dir_literal_when_none_is_resolved()
    {
        // A context with no install dir (e.g. the step unit tests) leaves the token
        // untouched rather than throwing — only the real From-built context resolves it.
        StepContext.Empty.Resolve("{install_dir}/x").Should().Be("{install_dir}/x");
    }

    // --- Token substitution in expressions (StepContext.Evaluate) ---

    [Fact]
    public void Evaluate_substitutes_install_dir_token_before_evaluating()
    {
        var ctx = new StepContext(new Dictionary<string, object?>(), installDir: "PICKED");

        ctx.Evaluate("'{install_dir}' == 'PICKED'").Should().BeTrue();
        ctx.Evaluate("'{install_dir}' == 'OTHER'").Should().BeFalse();
    }

    // --- Full From → engine flow: /D= relocation + literal-folder regression ---

    [Fact]
    public async Task D_override_lands_files_under_the_target_not_a_literal_install_dir_folder()
    {
        using var target = new TempDir();
        var container = PayloadExtractionTests.BuildPayload(("app/app.txt", "HELLO"));
        var extraction = PayloadExtraction.Extract(container, "t13" + Guid.NewGuid().ToString("N"));

        var literal = Path.Combine(Directory.GetCurrentDirectory(), "{install_dir}");
        try
        {
            var blob = new WrapperBlob(
                AppId: "com.acme.Studio",
                Parameters: Array.Empty<ParameterDefinition>(),
                InstallSteps: new InstallStep[]
                {
                    new InstallStep.FileCopy("copy", "payload://app/app.txt", "{install_dir}",
                        Overwrite: true, When: null, OnFailure: OnFailure.Fail),
                },
                PreInstall: Array.Empty<InstallStep>(),
                PostInstall: Array.Empty<InstallStep>(),
                UpdateSteps: Array.Empty<InstallStep>(),
                Scope: InstallScope.Auto,
                Options: null,
                AppName: "Acme Studio",
                InstallDir: "{scope_root}/Acme Studio");

            var parsed = CommandLineParser.Parse(new[] { "/silent", "/D=" + target.Path }, blob.Parameters);
            var ctx = StepContext.From(blob, parsed, extraction.Root, scope: InstallScope.User);

            ctx.InstallDir.Should().Be(Path.GetFullPath(target.Path), "/D= must set the effective install dir");

            var result = await new InstallEngine().RunAsync(blob.InstallSteps, ctx);

            result.Success.Should().BeTrue();
            File.Exists(Path.Combine(target.Path, "app.txt"))
                .Should().BeTrue("the file must land under the /D= target");
            Directory.Exists(literal)
                .Should().BeFalse("the literal-{install_dir} regression must be fixed");
        }
        finally
        {
            extraction.Dispose();
            if (Directory.Exists(literal))
            {
                Directory.Delete(literal, recursive: true);
            }
        }
    }

    [Fact]
    public void From_default_install_dir_reflects_scope_and_app_name()
    {
        var blob = MakeBlob(appName: "Acme Studio", installDir: null);
        var parsed = CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters);

        var user = StepContext.From(blob, parsed, scope: InstallScope.User);
        user.InstallDir.Should().Be(
            Path.Combine(ScopeLayout.For(InstallScope.User).InstallRoot, "Acme Studio"));

        var machine = StepContext.From(blob, parsed, scope: InstallScope.Machine);
        machine.InstallDir.Should().Be(
            Path.Combine(ScopeLayout.For(InstallScope.Machine).InstallRoot, "Acme Studio"));
    }

    // --- InstallSession seams the host uses ---

    [Fact]
    public void Session_ResolveDefaultInstallDir_honors_D_and_manifest()
    {
        var blob = MakeBlob(appName: "Acme Studio", installDir: "{scope_root}/Acme Studio");

        var withD = InstallSession.ForTesting(
            blob, CommandLineParser.Parse(new[] { "/silent", "/D=" + Path.Combine("C:", "Tools", "Acme") }, blob.Parameters));
        withD.ResolveDefaultInstallDir().Should().Be(Path.GetFullPath(Path.Combine("C:", "Tools", "Acme")));

        var noFlag = InstallSession.ForTesting(blob, CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters));
        noFlag.ResolveDefaultInstallDir(InstallScope.User)
            .Should().Be(Path.Combine(ScopeLayout.For(InstallScope.User).InstallRoot, "Acme Studio"));
    }

    [Fact]
    public void Session_ScopeIsSelectable_tracks_manifest_scope()
    {
        var silent = new[] { "/silent" };
        InstallSession.ForTesting(MakeBlob(scope: InstallScope.Auto),
            CommandLineParser.Parse(silent, Array.Empty<ParameterDefinition>())).ScopeIsSelectable.Should().BeTrue();
        InstallSession.ForTesting(MakeBlob(scope: InstallScope.User),
            CommandLineParser.Parse(silent, Array.Empty<ParameterDefinition>())).ScopeIsSelectable.Should().BeFalse();
        InstallSession.ForTesting(MakeBlob(scope: InstallScope.Machine),
            CommandLineParser.Parse(silent, Array.Empty<ParameterDefinition>())).ScopeIsSelectable.Should().BeFalse();
    }

    private static WrapperBlob MakeBlob(
        string? appName = "Acme Studio", string? installDir = null, InstallScope scope = InstallScope.Auto) =>
        new(
            AppId: "com.acme.Studio",
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: Array.Empty<InstallStep>(),
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Scope: scope,
            Options: null,
            AppName: appName,
            InstallDir: installDir);
}
