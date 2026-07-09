using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// T10 re-install / upgrade idempotency: two consecutive <c>/silent</c> installs of
/// the same app must not duplicate PATH entries, shortcuts, or ARP rows. The driver
/// detects the prior install (recorded state / ARP) and replays its recorded
/// uninstall before the fresh install re-lays every mutation exactly once
/// (uninstall-then-install, per T10).
/// </summary>
/// <remarks>
/// The PATH-duplication case is exercised against a DEDICATED, uniquely-named user
/// environment variable (append semantics identical to a real PATH append) rather
/// than the machine's actual <c>PATH</c>, so the test can never corrupt the host's
/// environment. Windows-only (real HKCU registry + shell-link COM); a no-op elsewhere.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ReinstallIdempotencyTests
{
    private const string UninstallRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static WrapperBlob Blob(string appId, string envVarName, string envValue, string shortcutDir) => new(
        AppId: appId,
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: new InstallStep[]
        {
            // PATH-style append to a throwaway user env var — the exact code path a
            // real PATH append uses. A non-idempotent second install would duplicate.
            new InstallStep.EnvSet(
                Id: "path",
                Name: envVarName,
                Value: envValue,
                Scope: "user",
                Action: "append",
                Separator: ";",
                When: null,
                OnFailure: OnFailure.Fail),
            // A desktop-style shortcut at a fixed path in a temp folder.
            new InstallStep.ShortcutCreate(
                Id: "shortcut",
                Target: envValue,
                Location: shortcutDir,
                Name: "Acme Studio",
                Args: null,
                WorkingDir: null,
                Icon: null,
                Description: null,
                When: null,
                OnFailure: OnFailure.Fail),
        },
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: InstallScope.User,
        DisplayName: "Acme Studio",
        Publisher: "Acme, Inc.",
        Version: "3.2.0",
        EstimatedSizeBytes: 4096);

    private static async Task InstallOnceAsync(WrapperBlob blob)
    {
        var parsed = CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        var outcome = await session.RunInstallCoreAsync(Array.Empty<byte>(), progress: null);
        outcome.Success.Should().BeTrue("the synthetic install pipeline must complete");
    }

    [Fact]
    public async Task Double_silent_install_does_not_duplicate_PATH_shortcuts_or_ARP()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "sigil.reinstall." + Guid.NewGuid().ToString("N");
        var envVarName = "SIGIL_T10_PATH_" + Guid.NewGuid().ToString("N");
        var shortcutDir = Path.Combine(Path.GetTempPath(), "sigil-reinstall-" + Guid.NewGuid().ToString("N"));
        var envValue = Path.Combine(shortcutDir, "bin");
        var installDir = Path.Combine(ScopeLayout.For(InstallScope.User).InstallRoot, appId);

        var blob = Blob(appId, envVarName, envValue, shortcutDir);

        try
        {
            await InstallOnceAsync(blob);
            await InstallOnceAsync(blob); // second consecutive install — must stay idempotent.

            // (a) PATH: the value appears exactly once, not twice.
            using (var env = Registry.CurrentUser.OpenSubKey("Environment"))
            {
                var raw = env?.GetValue(envVarName) as string ?? string.Empty;
                var occurrences = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
                occurrences.Should().ContainSingle(p => string.Equals(p, envValue, StringComparison.OrdinalIgnoreCase),
                    "a second install must not append the same PATH entry twice");
            }

            // (b) Shortcuts: exactly one .lnk in the location.
            Directory.GetFiles(shortcutDir, "*.lnk").Should().HaveCount(1,
                "the shortcut is overwritten in place, never duplicated");

            // (c) ARP: exactly one row, carrying the REAL name/version/publisher.
            using (var arp = Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}"))
            {
                arp.Should().NotBeNull("the reinstall must leave exactly one ARP row present");
                arp!.GetValue("DisplayName").Should().Be("Acme Studio");
                arp.GetValue("DisplayVersion").Should().Be("3.2.0");
                arp.GetValue("Publisher").Should().Be("Acme, Inc.");
                (arp.GetValue("EstimatedSize") as int?).Should().BeGreaterThan(0);
            }
        }
        finally
        {
            Cleanup(appId, envVarName, shortcutDir, installDir);
        }
    }

    private static void Cleanup(string appId, string envVarName, string shortcutDir, string installDir)
    {
#pragma warning disable CA1031 // Best-effort test cleanup.
        try { ArpRegistration.Remove(appId, InstallScope.User); } catch { }
        try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { }
        try
        {
            using var env = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
            if (env?.GetValue(envVarName) is not null) env.DeleteValue(envVarName, throwOnMissingValue: false);
        }
        catch { }
        try { if (Directory.Exists(shortcutDir)) Directory.Delete(shortcutDir, recursive: true); } catch { }
        try { if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true); } catch { }
#pragma warning restore CA1031
    }
}
