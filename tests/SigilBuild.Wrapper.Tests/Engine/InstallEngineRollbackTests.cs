using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

public class InstallEngineRollbackTests
{
    [Fact]
    public async Task Mid_step_failure_rolls_back_already_copied_files()
    {
        using var tempSrc = new TempDir();
        using var tempDst = new TempDir();
        File.WriteAllText(Path.Combine(tempSrc.Path, "a.txt"), "1");
        File.WriteAllText(Path.Combine(tempSrc.Path, "b.txt"), "2");

        // The 2nd step references a non-existent step type, which the StepFactory
        // throws on, simulating a programming error. The engine should catch it,
        // walk the journal back, and remove the files copied by step 1.
        var steps = new InstallStep[]
        {
            new InstallStep.FileCopy("s1",
                From: Path.Combine(tempSrc.Path, "*.txt"),
                To: tempDst.Path,
                Overwrite: true,
                When: null,
                OnFailure: OnFailure.Rollback),
            new InstallStep.RegistryWrite("s2", "HKLM", "K", "N", "REG_SZ", "V", "native", null, OnFailure.Rollback),
        };

        var engine = new InstallEngine();
        var result = await engine.RunAsync(steps, StepContext.Empty);

        result.Success.Should().BeFalse();
        Directory.GetFiles(tempDst.Path).Should().BeEmpty(
            "rollback must remove every file copied by s1 after s2 throws");
    }

    [Fact]
    public async Task When_clause_skips_step()
    {
        using var tempDst = new TempDir();
        var target = Path.Combine(tempDst.Path, "skipped");

        var ctx = new StepContext(new System.Collections.Generic.Dictionary<string, object?>
        {
            ["parameters.edition"] = "community",
        });

        var steps = new InstallStep[]
        {
            new InstallStep.DirectoryCreate("s1", target,
                When: "parameters.edition == 'pro'",
                OnFailure: OnFailure.Fail),
        };

        var result = await new InstallEngine().RunAsync(steps, ctx);

        result.Success.Should().BeTrue();
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task OnFailure_Continue_proceeds_without_rollback()
    {
        using var tempDst = new TempDir();
        var dirAfter = Path.Combine(tempDst.Path, "after");

        // First step: a file_copy whose source root does not exist — fails with
        // OnFailure.Continue, so the engine should keep going, run the dir step,
        // and return success.
        var steps = new InstallStep[]
        {
            new InstallStep.FileCopy("s1",
                From: Path.Combine(tempDst.Path, "no-such-dir", "*.txt"),
                To: tempDst.Path,
                Overwrite: true,
                When: null,
                OnFailure: OnFailure.Continue),
            new InstallStep.DirectoryCreate("s2", dirAfter, When: null, OnFailure: OnFailure.Fail),
        };

        var result = await new InstallEngine().RunAsync(steps, StepContext.Empty);

        result.Success.Should().BeTrue();
        Directory.Exists(dirAfter).Should().BeTrue("Continue must not abort the run");
    }
}
