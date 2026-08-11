namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// The shipped example manifests must actually install under the R3/R9/R16
/// guards, not merely satisfy the JSON schema.
/// </summary>
/// <remarks>
/// <para>
/// This closes the gap that let lane S2 break its own documentation. CI's example
/// gate (<c>pr-guards.yml</c>) validates <c>examples/**</c> against
/// <c>schemas/sigil-schema.json</c> and nothing else — the schema has no opinion
/// about whether a resolved path lands inside <c>install_dir</c>, so every
/// containment refusal these tests catch was invisible to it. Both examples aborted
/// on their first <c>file_copy</c> and the suite stayed green.
/// </para>
/// <para>
/// What is checked here is the step of the pipeline the guards live in: parse the
/// real manifest file, build the same <see cref="StepContext"/> a real run builds,
/// resolve each step's path fields exactly as the step does, and assert the guards
/// admit them. One example additionally runs its <c>file_copy</c> through the real
/// <see cref="InstallEngine"/> and asserts the payload lands.
/// </para>
/// <para>
/// Not covered, and deliberately so: no test here executes a
/// <c>shortcut_create</c>, <c>registry_write</c> or any privileged step from an
/// example, because those mutate the machine running the suite. Their paths are
/// resolved and guard-checked; their effects are not produced.
/// </para>
/// </remarks>
public sealed class ShippedExampleInstallTests
{
    public static TheoryData<string> Examples() => new()
    {
        "examples/exe-wrapper/hello-wix-killer/sigil.yaml",
        "examples/exe-wrapper/multi-edition/sigil.yaml",
    };

    [WindowsTheory("Windows path semantics")]
    [MemberData(nameof(Examples))]
    public void Every_example_step_destination_resolves_and_passes_the_guards(string relativePath)
    {
        var manifest = ParseOrFail(relativePath);
        using var installDir = new TempDir();
        var ctx = ContextFor(manifest, installDir.Path);

        foreach (var step in AllSteps(manifest))
        {
            foreach (var (field, raw) in PathFieldsOf(step))
            {
                // Resolving is itself part of the assertion: ResolvePath throws on a
                // path that still carries an unresolved {token}, which is how the
                // '%ProgramFiles%' idiom used to fail — silently, as a directory
                // named after the template text.
                var resolved = ResolveOrFail(ctx, step.Id, field, raw);

                resolved.Should().NotContain("%",
                    $"step '{step.Id}' field '{field}': there is no environment-variable expansion " +
                    "in a step path, so a '%VAR%' here would be taken literally");

                if (IsContainedDestination(step, field))
                {
                    StepDestinationGuard.Check(
                            ctx.InstallDir, step.GetType().Name, field, resolved, step.AllowOutsideInstallDir)
                        .Should().BeNull(
                            $"step '{step.Id}' field '{field}' resolved to '{resolved}', which the " +
                            "install-time containment guard would refuse — this example would abort");
                }
            }
        }
    }

    [WindowsFact("Windows path semantics")]
    public async Task Hello_wix_killer_copies_its_payload_into_the_resolved_install_dir()
    {
        // The end-to-end leg for the step that used to abort. `to` is taken
        // verbatim from the shipped manifest — it is the field under test. `from`
        // is rebased onto a temp payload because the manifest's 'payload/**' is
        // relative to the packaging working directory, which a unit test has no
        // business changing (it is process-global and this suite runs in parallel).
        var manifest = ParseOrFail("examples/exe-wrapper/hello-wix-killer/sigil.yaml");
        var copy = AllSteps(manifest).OfType<InstallStep.FileCopy>().First(s => s.Id == "copy-app");

        using var payload = new TempDir();
        using var installDir = new TempDir();
        File.WriteAllText(Path.Combine(payload.Path, "app.exe"), "payload bytes");

        var result = await new InstallEngine().RunAsync(
            new InstallStep[]
            {
                copy with { From = Path.Combine(payload.Path, "**") },
            },
            ContextFor(manifest, installDir.Path),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        File.ReadAllText(Path.Combine(installDir.Path, "app.exe")).Should().Be("payload bytes");
    }

    [WindowsTheory("Windows path semantics")]
    [MemberData(nameof(Examples))]
    public void No_example_declares_a_parameter_that_shadows_the_install_dir(string relativePath)
    {
        // The specific mistake that broke both examples. A parameter named
        // 'install_dir' is a second, unrelated value: it is not ctx.InstallDir, so
        // it does not follow the wizard's Destination screen, /D=, or an
        // upgrade-in-place, and the guards anchor on ctx.InstallDir.
        var manifest = ParseOrFail(relativePath);

        (manifest.Parameters?.Values ?? Enumerable.Empty<ParameterDefinition>())
            .Select(p => p.Name)
            .Should().NotContain(
                "install_dir",
                "the destination is '{install_dir}', the installer's own resolved value — a " +
                "same-named parameter silently diverges from it");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SigilManifest ParseOrFail(string relativePath)
    {
        var full = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(full).Should().BeTrue($"the shipped example '{relativePath}' must exist");

        var result = ManifestParser.Parse(File.ReadAllText(full), full);
        result.Diagnostics.Should().NotContain(
            d => d.Severity == DiagnosticSeverity.Error,
            $"'{relativePath}' must parse cleanly: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
        return result.Manifest!;
    }

    /// <summary>
    /// A context anchored on <paramref name="installDir"/> with every declared
    /// parameter seeded at its default — the values a silent install with no
    /// overrides would use.
    /// </summary>
    private static StepContext ContextFor(SigilManifest manifest, string installDir)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in (manifest.Parameters?.Values ?? Enumerable.Empty<ParameterDefinition>()))
        {
            values["parameters." + p.Name] = p.Default;
            values["param." + p.Name] = p.Default;
        }

        return new StepContext(
            values,
            installDir: installDir,
            appName: manifest.App.Name,
            appId: manifest.App.Id);
    }

    private static string ResolveOrFail(StepContext ctx, string id, string field, string raw)
    {
        try
        {
            return ctx.ResolvePath(raw);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"step '{id}' field '{field}' ('{raw}') does not resolve: {ex.Message}", ex);
        }
    }

    private static IEnumerable<InstallStep> AllSteps(SigilManifest m) =>
        (m.InstallSteps ?? Enumerable.Empty<InstallStep>())
            .Concat(m.PreInstall ?? Enumerable.Empty<InstallStep>())
            .Concat(m.PostInstall ?? Enumerable.Empty<InstallStep>());

    /// <summary>Every path-valued field of a step, by step type.</summary>
    private static IEnumerable<(string Field, string Raw)> PathFieldsOf(InstallStep step)
    {
        switch (step)
        {
            case InstallStep.FileCopy x:
                yield return ("to", x.To);
                break;
            case InstallStep.DirectoryCreate x:
                yield return ("path", x.Path);
                break;
            case InstallStep.FileDelete x:
                yield return ("path", x.Path);
                break;
            case InstallStep.DirectoryDelete x:
                yield return ("path", x.Path);
                break;
            case InstallStep.HttpDownload x:
                yield return ("dest", x.Dest);
                break;
            case InstallStep.IniWrite x:
                yield return ("path", x.Path);
                break;
            case InstallStep.JsonEdit x:
                yield return ("path", x.Path);
                break;
            case InstallStep.XmlEdit x:
                yield return ("path", x.Path);
                break;
            case InstallStep.ShortcutCreate x:
                yield return ("target", x.Target);
                // 'location' resolves only when it is not a named anchor.
                if (x.Location is not ("start_menu" or "desktop"))
                {
                    yield return ("location", x.Location);
                }
                if (x.WorkingDir is not null) { yield return ("working_dir", x.WorkingDir); }
                if (x.Icon is not null) { yield return ("icon", x.Icon); }
                break;
            case InstallStep.RunProgram x:
                yield return ("program", x.Program);
                if (x.Cwd is not null) { yield return ("cwd", x.Cwd); }
                break;
            case InstallStep.ServiceInstall x:
                yield return ("binary_path", x.BinaryPath);
                break;
            case InstallStep.ScheduledTaskCreate x:
                yield return ("program", x.Program);
                break;
            case InstallStep.ComRegister x:
                yield return ("path", x.Path);
                break;
            case InstallStep.FirewallRule x:
                if (x.Program is not null) { yield return ("program", x.Program); }
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// The step types whose destination the R16 containment guard anchors. Mirrors
    /// <c>ManifestParser.ContainedDestinationStepTypes</c>; <c>shortcut_create</c>
    /// is absent on purpose (its named anchors are outside install_dir by design).
    /// </summary>
    private static bool IsContainedDestination(InstallStep step, string field) => step switch
    {
        InstallStep.FileCopy => field == "to",
        InstallStep.DirectoryCreate or InstallStep.FileDelete or InstallStep.DirectoryDelete
            or InstallStep.IniWrite or InstallStep.JsonEdit or InstallStep.XmlEdit => field == "path",
        InstallStep.HttpDownload => field == "dest",
        _ => false,
    };

    /// <summary>
    /// Walk up from the test assembly to the directory holding <c>Sigil.slnx</c>,
    /// so the examples are read from the working tree rather than from a copy.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sigil.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the tests must be able to locate the repository root");
        return dir!.FullName;
    }
}
