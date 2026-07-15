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
/// P2 (gap G2): lifecycle hooks (<c>installer.hooks</c>) run OUTSIDE the rollback
/// journal, around the transactional body, with per-step on_failure. Driven end to
/// end through <see cref="InstallSession"/> with /LOG so ordering and the
/// non-rollback semantics are observable.
/// </summary>
public sealed class LifecycleHookTests
{
    private static InstallStep.DirectoryCreate Mkdir(string id, string path, OnFailure onFailure)
        => new(id, path, When: null, onFailure);

    // A step that always fails (glob root does not exist), used to exercise a
    // failing hook without needing Windows-specific programs.
    private static InstallStep.FileCopy FailingCopy(string id, string tmpRoot, OnFailure onFailure)
        => new(
            id,
            Path.Combine(tmpRoot, "nope-" + Guid.NewGuid().ToString("N"), "*"),
            Path.Combine(tmpRoot, "dst"),
            Overwrite: false, When: null, onFailure);

    private static async Task<(int Code, string Log)> RunSilentAsync(
        WrapperBlob blob, string logPath, params string[] extra)
    {
        var args = new List<string> { "/silent", $"/LOG={logPath}" };
        args.AddRange(extra);
        var parsed = CommandLineParser.Parse(args.ToArray(), blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());
        var log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        return (code, log);
    }

    private static WrapperBlob Blob(
        string appId,
        IReadOnlyList<InstallStep> install,
        IReadOnlyList<InstallStep>? preInstall = null,
        IReadOnlyList<InstallStep>? postInstall = null,
        IReadOnlyList<InstallStep>? preUninstall = null,
        IReadOnlyList<InstallStep>? postUninstall = null)
        => new(
            AppId: appId,
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: install,
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            HookPreInstall: preInstall,
            HookPostInstall: postInstall,
            HookPreUninstall: preUninstall,
            HookPostUninstall: postUninstall);

    private static void Cleanup(string appId)
    {
#pragma warning disable CA1031 // test cleanup best-effort
        try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { }
        if (OperatingSystem.IsWindows())
        {
            try { SigilBuild.Wrapper.Cli.ArpRegistration.Remove(appId, InstallScope.User); } catch { }
        }
#pragma warning restore CA1031
    }

    [Fact]
    public async Task Ordering_is_pre_install_then_body_then_post_install()
    {
        using var tmp = new TempDir();
        var appId = "com.acme.p2order-" + Guid.NewGuid().ToString("N");
        var log = Path.Combine(tmp.Path, "o.log");
        var pre = Path.Combine(tmp.Path, "predir");
        var body = Path.Combine(tmp.Path, "bodydir");
        var post = Path.Combine(tmp.Path, "postdir");

        var blob = Blob(appId,
            install: new[] { Mkdir("body", body, OnFailure.Fail) },
            preInstall: new[] { Mkdir("pre", pre, OnFailure.Fail) },
            postInstall: new[] { Mkdir("post", post, OnFailure.Continue) });

        try
        {
            var (code, logText) = await RunSilentAsync(blob, log);

            code.Should().Be(0);
            Directory.Exists(pre).Should().BeTrue();
            Directory.Exists(body).Should().BeTrue();
            Directory.Exists(post).Should().BeTrue();

            // The /LOG file records the three in order: pre hook, body (engine), post hook.
            var iPre = logText.IndexOf("predir", StringComparison.Ordinal);
            var iBody = logText.IndexOf("bodydir", StringComparison.Ordinal);
            var iPost = logText.IndexOf("postdir", StringComparison.Ordinal);
            iPre.Should().BeGreaterThan(-1);
            iPre.Should().BeLessThan(iBody, "pre_install runs before the journaled body");
            iBody.Should().BeLessThan(iPost, "post_install runs after the journaled body");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task Failing_pre_install_aborts_before_the_journal_opens()
    {
        using var tmp = new TempDir();
        var appId = "com.acme.p2abort-" + Guid.NewGuid().ToString("N");
        var log = Path.Combine(tmp.Path, "a.log");
        var body = Path.Combine(tmp.Path, "bodydir");

        var blob = Blob(appId,
            install: new[] { Mkdir("body", body, OnFailure.Fail) },
            preInstall: new[] { FailingCopy("badpre", tmp.Path, OnFailure.Fail) });

        try
        {
            var (code, logText) = await RunSilentAsync(blob, log);

            code.Should().Be(1);
            Directory.Exists(body).Should().BeFalse("the journal must never open when pre_install fails");
            logText.Should().Contain("hook pre_install");
            logText.Should().Contain("aborted before install");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task Failing_post_install_with_continue_still_exits_0_and_logs()
    {
        using var tmp = new TempDir();
        var appId = "com.acme.p2post-" + Guid.NewGuid().ToString("N");
        var log = Path.Combine(tmp.Path, "p.log");
        var body = Path.Combine(tmp.Path, "bodydir");

        var blob = Blob(appId,
            install: new[] { Mkdir("body", body, OnFailure.Fail) },
            postInstall: new[] { FailingCopy("badpost", tmp.Path, OnFailure.Continue) });

        try
        {
            var (code, logText) = await RunSilentAsync(blob, log);

            code.Should().Be(0, "a post_install failure with continue is non-fatal — the install is committed");
            Directory.Exists(body).Should().BeTrue("the install was committed, not rolled back");
            logText.Should().Contain("hook post_install");
            logText.Should().Contain("(continue)");
            logText.Should().Contain("result: success");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task Failing_pre_uninstall_aborts_the_uninstall()
    {
        using var tmp = new TempDir();
        var appId = "com.acme.p2preu-" + Guid.NewGuid().ToString("N");
        var log = Path.Combine(tmp.Path, "u.log");

        var blob = Blob(appId,
            install: Array.Empty<InstallStep>(),
            preUninstall: new[] { FailingCopy("badpreu", tmp.Path, OnFailure.Fail) });

        var parsed = CommandLineParser.Parse(new[] { "/silent", "/Uninstall", $"/LOG={log}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

        code.Should().Be(1);
        var logText = File.ReadAllText(log);
        logText.Should().Contain("hook pre_uninstall");
        logText.Should().Contain("uninstall aborted");
    }

    [Fact]
    public async Task Post_uninstall_runs_after_a_successful_uninstall()
    {
        using var tmp = new TempDir();
        var appId = "com.acme.p2postu-" + Guid.NewGuid().ToString("N");
        var log = Path.Combine(tmp.Path, "pu.log");
        var marker = Path.Combine(tmp.Path, "postu-marker");

        // Seed uninstall state so UninstallEngine finds something to replay.
        UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User, Array.Empty<string>());
        try
        {
            var blob = Blob(appId,
                install: Array.Empty<InstallStep>(),
                postUninstall: new[] { Mkdir("pu", marker, OnFailure.Continue) });

            var parsed = CommandLineParser.Parse(new[] { "/silent", "/Uninstall", $"/LOG={log}" }, blob.Parameters);
            var session = InstallSession.ForTesting(blob, parsed);

            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            code.Should().Be(0);
            Directory.Exists(marker).Should().BeTrue("post_uninstall runs after the journal replay");
            File.ReadAllText(log).Should().Contain("hook post_uninstall");
        }
        finally
        {
            Cleanup(appId);
        }
    }
}
