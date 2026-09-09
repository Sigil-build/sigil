using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// P8: end-to-end coverage for the ini_write / json_edit / xml_edit steps —
/// create_if_missing modes, byte-exact rollback, created-file removal, a /silent
/// run exercising all three, and secret redaction in the log.
/// </summary>
public sealed class ConfigStepIntegrationTests
{
    private static InstallStep.FileCopy FailingStep(string tmp) =>
        new("boom", Path.Combine(tmp, "nope-" + Guid.NewGuid().ToString("N"), "*"),
            Path.Combine(tmp, "dst"), Overwrite: false, When: null, OnFailure.Rollback);

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

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
    public async Task Missing_file_fails_without_create_and_creates_with_it()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "app.ini");

        var noCreate = await new InstallEngine().RunAsync(
            new[] { new InstallStep.IniWrite("i", path, "app", "k", "v", CreateIfMissing: false, null, OnFailure.Fail) },
            StepContext.Empty);
        noCreate.Success.Should().BeFalse();
        File.Exists(path).Should().BeFalse();

        var create = await new InstallEngine().RunAsync(
            new[] { new InstallStep.IniWrite("i", path, "app", "k", "v", CreateIfMissing: true, null, OnFailure.Fail) },
            StepContext.Empty);
        create.Success.Should().BeTrue();
        File.ReadAllText(path).Should().Contain("[app]").And.Contain("k=v");
    }

    [Fact]
    public async Task Rollback_restores_the_prior_file_byte_exact()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "settings.json");
        File.WriteAllText(path, """{ "keep": "me", "level": "info" }""");
        var originalHash = Sha256(path);

        var result = await new InstallEngine().RunAsync(
            new InstallStep[]
            {
                new InstallStep.JsonEdit("j", path, "/level", "debug", CreateIfMissing: false, null, OnFailure.Rollback),
                FailingStep(tmp.Path),
            },
            StepContext.Empty);

        result.Success.Should().BeFalse();
        Sha256(path).Should().Be(originalHash, "rollback restores the exact original bytes");
    }

    [Fact]
    public async Task Rollback_removes_a_created_file()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "new.xml");

        var result = await new InstallEngine().RunAsync(
            new InstallStep[]
            {
                new InstallStep.XmlEdit("x", path, "/config/level", null, "debug", CreateIfMissing: true, null, OnFailure.Rollback),
                FailingStep(tmp.Path),
            },
            StepContext.Empty);

        result.Success.Should().BeFalse();
        File.Exists(path).Should().BeFalse("rollback removes a file the edit created");
    }

    [Fact]
    public async Task Silent_install_applies_all_three_config_edits()
    {
        using var tmp = new TempDir();
        var ini = Path.Combine(tmp.Path, "a.ini");
        var json = Path.Combine(tmp.Path, "a.json");
        var xml = Path.Combine(tmp.Path, "a.xml");
        File.WriteAllText(ini, "[app]\nx=1\n");
        File.WriteAllText(json, """{"a":1}""");
        File.WriteAllText(xml, "<root><a>old</a></root>");

        var appId = "com.acme.p8-" + Guid.NewGuid().ToString("N");
        try
        {
            var blob = new WrapperBlob(
                AppId: appId,
                Parameters: Array.Empty<ParameterDefinition>(),
                // R16 contains every step destination to install_dir. These three
                // edit files in an OS temp directory, which no real silent install
                // resolves as install_dir — so the fixture declares the out-of-tree
                // write with the very per-step manifest opt-out a publisher editing
                // %ProgramData% would use. The production rule is not relaxed;
                // StepDestinationContainmentTests exercises it directly.
                InstallSteps: new InstallStep[]
                {
                    new InstallStep.IniWrite("i", ini, "app", "x", "9", false, null, OnFailure.Fail)
                        { AllowOutsideInstallDir = true },
                    new InstallStep.JsonEdit("j", json, "/a", "2", false, null, OnFailure.Fail)
                        { AllowOutsideInstallDir = true },
                    // R35: `value_type` must survive the blob wire, not only the
                    // in-process editor. This step lands as a NUMBER only if the flag
                    // reached the runtime; "j" above lands as a STRING because the
                    // default is now `string`.
                    new InstallStep.JsonEdit(
                        "j2", json, "/b", "7", false, null, OnFailure.Fail, JsonValueType.Json)
                        { AllowOutsideInstallDir = true },
                    new InstallStep.XmlEdit("x", xml, "/root/a", null, "new", false, null, OnFailure.Fail)
                        { AllowOutsideInstallDir = true },
                },
                PreInstall: Array.Empty<InstallStep>(),
                PostInstall: Array.Empty<InstallStep>(),
                UpdateSteps: Array.Empty<InstallStep>());
            var parsed = CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters);
            var session = InstallSession.ForTesting(blob, parsed);

            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            code.Should().Be(0);
            File.ReadAllText(ini).Should().Contain("x=9");
            File.ReadAllText(json).Should().Contain("\"a\": \"2\"",
                "value_type defaults to string (R35)");
            File.ReadAllText(json).Should().Contain("\"b\": 7",
                "value_type: json must round-trip through the signed blob (R35)");
            File.ReadAllText(xml).Should().Contain("<a>new</a>");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task Secret_value_lands_in_the_file_but_is_absent_from_the_log()
    {
        const string Secret = "TKN-9999-SECRET";
        using var tmp = new TempDir();
        var ini = Path.Combine(tmp.Path, "a.ini");
        var log = Path.Combine(tmp.Path, "install.log");
        File.WriteAllText(ini, "[auth]\ntoken=placeholder\n");

        var appId = "com.acme.p8secret-" + Guid.NewGuid().ToString("N");
        try
        {
            var blob = new WrapperBlob(
                AppId: appId,
                Parameters: new[]
                {
                    new ParameterDefinition("token", ParameterType.Secret, null, null, true, LocalizedText.Plain("k"), null, null, null),
                },
                InstallSteps: new InstallStep[]
                {
                    // R16: an OS temp directory is never install_dir — see the
                    // note in Silent_install_applies_all_three_config_edits.
                    new InstallStep.IniWrite("i", ini, "auth", "token", "${parameters.token}", false, null, OnFailure.Fail)
                        { AllowOutsideInstallDir = true },
                },
                PreInstall: Array.Empty<InstallStep>(),
                PostInstall: Array.Empty<InstallStep>(),
                UpdateSteps: Array.Empty<InstallStep>());
            var parsed = CommandLineParser.Parse(new[] { "/silent", $"/Ptoken={Secret}", $"/LOG={log}" }, blob.Parameters);
            var session = InstallSession.ForTesting(blob, parsed);

            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            code.Should().Be(0);
            File.ReadAllText(ini).Should().Contain(Secret, "the resolved value is written to the config file");
            File.ReadAllText(log).Should().NotContain(Secret, "the /LOG file must not leak the secret value");
        }
        finally
        {
            Cleanup(appId);
        }
    }
}
