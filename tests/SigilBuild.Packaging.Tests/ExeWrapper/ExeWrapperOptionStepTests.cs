using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// T8: the pack → blob half of the built-in Options feature.
/// <see cref="ExeWrapperPackager.BuildBlobBytes"/> auto-generates an
/// <c>option.&lt;component&gt;</c>-gated install step for every ENABLED component,
/// carries the enabled component list in the blob, and generates NOTHING for a
/// disabled component.
/// </summary>
public class ExeWrapperOptionStepTests
{
    private static SerializableWrapperBlob Deserialize(byte[] blob) =>
        JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(blob),
            WrapperBlobJsonContext.Default.SerializableWrapperBlob)!;

    private static SigilManifest ManifestWith(InstallerOptions options) => new(
        "v1.0",
        new AppSection("com.acme.Studio", "Acme Studio", "3.2.0", "Acme, Inc.", null, null),
        new BuildSection("./out", null, null, true),
        null, null, null, null,
        Installer: new InstallerSection(null, Options: options),
        Location: SourceLocation.Unknown);

    [Fact]
    public void Enabled_components_generate_gated_steps_and_component_list()
    {
        var manifest = ManifestWith(new InstallerOptions(
            DesktopShortcut: new InstallerOption(Enabled: true, Default: true),
            AddToPath: new InstallerOption(Enabled: true, Default: true)));

        var s = Deserialize(ExeWrapperPackager.BuildBlobBytes(manifest, string.Empty));

        // A desktop ShortcutCreate gated on option.desktop_shortcut.
        s.InstallSteps.Should().ContainSingle(step =>
            step.Type == "shortcut_create"
            && step.Location == "desktop"
            && step.When == "option.desktop_shortcut");

        // An EnvSet appending PATH gated on option.add_to_path.
        s.InstallSteps.Should().ContainSingle(step =>
            step.Type == "env_set"
            && step.When == "option.add_to_path");

        // The enabled component list travels in the blob for the runtime + wizard.
        s.Options.Select(o => o.Name).Should().BeEquivalentTo("desktop_shortcut", "add_to_path");
    }

    [Fact]
    public void Disabled_component_is_removed_entirely_at_pack_time()
    {
        var manifest = ManifestWith(new InstallerOptions(
            DesktopShortcut: new InstallerOption(Enabled: true, Default: true),
            StartMenu: new InstallerOption(Enabled: false)));

        var s = Deserialize(ExeWrapperPackager.BuildBlobBytes(manifest, string.Empty));

        // start_menu disabled → no generated step and no component.
        s.InstallSteps.Should().NotContain(step => step.Location == "start_menu");
        s.InstallSteps.Should().NotContain(step => step.When == "option.start_menu");
        s.Options.Select(o => o.Name).Should().NotContain("start_menu");
    }

    [Fact]
    public void File_associations_generate_one_registry_write_per_extension_sorted()
    {
        var manifest = ManifestWith(new InstallerOptions(
            FileAssociations: new FileAssociationOption(
                Enabled: true, Default: false, Extensions: new[] { ".zed", ".acme" })));

        var s = Deserialize(ExeWrapperPackager.BuildBlobBytes(manifest, string.Empty));

        var regSteps = s.InstallSteps
            .Where(step => step.Type == "registry_write" && step.When == "option.file_associations")
            .ToList();

        regSteps.Should().HaveCount(2);
        regSteps.Should().OnlyContain(step => step.Key!.StartsWith("Software\\Classes\\"));
        // Deterministic ordinal order: ".acme" before ".zed".
        regSteps[0].Key.Should().Be("Software\\Classes\\.acme");
        regSteps[1].Key.Should().Be("Software\\Classes\\.zed");
        regSteps[0].Value!.Value.GetString().Should().Be("com.acme.Studio.acme");
    }

    [Fact]
    public void Locked_component_is_carried_with_its_locked_flag()
    {
        var manifest = ManifestWith(new InstallerOptions(
            AddToPath: new InstallerOption(Enabled: true, Default: true, Locked: true)));

        var s = Deserialize(ExeWrapperPackager.BuildBlobBytes(manifest, string.Empty));

        var comp = s.Options.Single(o => o.Name == "add_to_path");
        comp.Locked.Should().BeTrue();
        comp.Default.Should().BeTrue();
        // The step is still generated for a locked component.
        s.InstallSteps.Should().Contain(step => step.When == "option.add_to_path");
    }

    [Fact]
    public void No_options_block_generates_no_option_steps()
    {
        var manifest = new SigilManifest(
            "v1.0",
            new AppSection("com.acme.Studio", "Acme Studio", "3.2.0", "Acme, Inc.", null, null),
            new BuildSection("./out", null, null, true),
            null, null, null, null,
            Installer: new InstallerSection(null),
            Location: SourceLocation.Unknown);

        var s = Deserialize(ExeWrapperPackager.BuildBlobBytes(manifest, string.Empty));

        s.Options.Should().BeEmpty();
        s.InstallSteps.Should().NotContain(step => step.When != null && step.When.StartsWith("option."));
    }
}
