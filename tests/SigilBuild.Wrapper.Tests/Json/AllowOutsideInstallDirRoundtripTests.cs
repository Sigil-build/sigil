using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Json;

/// <summary>
/// R16: the <c>allow_outside_install_dir</c> opt-out is an envelope field on the
/// base <see cref="InstallStep"/> record, so it has to survive the AOT-safe wire
/// form for every step type that can carry it — the runtime steps read it out of
/// the blob, not out of the manifest.
/// </summary>
public class AllowOutsideInstallDirRoundtripTests
{
    private static InstallStep RoundTrip(InstallStep step) =>
        SerializableInstallStepConverter.ToInstallStep(
            SerializableInstallStepConverter.FromInstallStep(step));

    [Fact]
    public void FileCopy_carries_the_opt_out()
    {
        var step = new InstallStep.FileCopy("cp", "payload://a", "{install_dir}", true, null, OnFailure.Fail)
        {
            AllowOutsideInstallDir = true,
        };

        RoundTrip(step).AllowOutsideInstallDir.Should().BeTrue();
        RoundTrip(step).Should().BeEquivalentTo(step);
    }

    [Fact]
    public void HttpDownload_carries_the_opt_out()
    {
        // The web-installer stub depends on exactly this: its synthesized
        // http_download lands outside install_dir and the blob is the only thing
        // that can say so.
        var step = new InstallStep.HttpDownload(
            "dl", "https://example.com/p.exe", "{temp_dir}/p.exe", new string('a', 64),
            null, 3, null, OnFailure.Fail)
        {
            AllowOutsideInstallDir = true,
        };

        RoundTrip(step).AllowOutsideInstallDir.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ini_json_xml_and_the_delete_steps_all_carry_it(bool allow)
    {
        InstallStep[] steps =
        {
            new InstallStep.IniWrite("i", "a.ini", "app", "k", "v", true, null, OnFailure.Fail)
                { AllowOutsideInstallDir = allow },
            new InstallStep.JsonEdit("j", "a.json", "/a", "1", true, null, OnFailure.Fail)
                { AllowOutsideInstallDir = allow },
            new InstallStep.XmlEdit("x", "a.xml", "/root/a", null, "v", true, null, OnFailure.Fail)
                { AllowOutsideInstallDir = allow },
            new InstallStep.FileDelete("fd", "a.txt", "skip", null, OnFailure.Fail)
                { AllowOutsideInstallDir = allow },
            new InstallStep.DirectoryDelete("dd", "a", true, null, OnFailure.Fail)
                { AllowOutsideInstallDir = allow },
        };

        foreach (var step in steps)
        {
            RoundTrip(step).AllowOutsideInstallDir.Should().Be(allow, step.Id);
        }
    }

    [Fact]
    public void A_step_that_never_set_it_writes_nothing_on_the_wire()
    {
        // Pack output must stay byte-stable for every manifest that does not use
        // the opt-out: the DTO property is left null, not written as false.
        var step = new InstallStep.FileCopy("cp", "payload://a", "{install_dir}", true, null, OnFailure.Fail);

        SerializableInstallStepConverter.FromInstallStep(step)
            .AllowOutsideInstallDir.Should().BeNull();
        RoundTrip(step).AllowOutsideInstallDir.Should().BeFalse();
    }
}
