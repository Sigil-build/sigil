using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// T8: parsing of <c>installer.options</c> — each of the four built-in components
/// is a shorthand boolean or an object <c>{ enabled, default, locked, ... }</c>
/// (<c>file_associations</c> adds <c>extensions</c>) — into the M0 records on
/// <see cref="InstallerSection.Options"/>.
/// </summary>
public class InstallerOptionsParseTests
{
    private static string Yaml(string optionsBlock) => $$"""
        spec: v1.0
        app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }
        build: { source: ./out }
        installer:
          options:
        {{optionsBlock}}
        """;

    private static InstallerOptions Parse(string optionsBlock)
    {
        var result = ManifestParser.Parse(Yaml(optionsBlock), "s.yaml");
        result.Diagnostics.Should().BeEmpty();
        result.Manifest!.Installer!.Options.Should().NotBeNull();
        return result.Manifest.Installer.Options!;
    }

    [Fact]
    public void Shorthand_true_enables_with_default_true()
    {
        var opts = Parse("    desktop_shortcut: true");

        opts.DesktopShortcut.Should().NotBeNull();
        opts.DesktopShortcut!.Enabled.Should().BeTrue();
        opts.DesktopShortcut.Default.Should().BeTrue();
        opts.DesktopShortcut.Locked.Should().BeFalse();
    }

    [Fact]
    public void Shorthand_false_disables_the_component()
    {
        var opts = Parse("    start_menu: false");

        opts.StartMenu.Should().NotBeNull();
        opts.StartMenu!.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Object_form_supplies_enabled_default_locked()
    {
        var opts = Parse("""
                add_to_path: { enabled: true, default: true, locked: true }
        """);

        opts.AddToPath.Should().NotBeNull();
        opts.AddToPath!.Enabled.Should().BeTrue();
        opts.AddToPath.Default.Should().BeTrue();
        opts.AddToPath.Locked.Should().BeTrue();
    }

    [Fact]
    public void Object_form_default_only_leaves_enabled_true()
    {
        var opts = Parse("""
                add_to_path: { default: true }
        """);

        opts.AddToPath!.Enabled.Should().BeTrue("an object form with no 'enabled' key is enabled by default");
        opts.AddToPath.Default.Should().BeTrue();
    }

    [Fact]
    public void File_associations_object_carries_extensions()
    {
        var opts = Parse("""
                file_associations: { enabled: true, extensions: [".acme", ".acme2"], default: false }
        """);

        opts.FileAssociations.Should().NotBeNull();
        opts.FileAssociations!.Enabled.Should().BeTrue();
        opts.FileAssociations.Default.Should().BeFalse();
        opts.FileAssociations.Extensions.Should().Equal(".acme", ".acme2");
    }

    [Fact]
    public void Reference_manifest_options_block_parses()
    {
        var opts = Parse("""
                desktop_shortcut: true
                add_to_path: { default: true }
                file_associations: { enabled: true, extensions: [".acme"], default: false }
                start_menu: false
        """);

        opts.DesktopShortcut!.Enabled.Should().BeTrue();
        opts.AddToPath!.Enabled.Should().BeTrue();
        opts.FileAssociations!.Enabled.Should().BeTrue();
        opts.FileAssociations.Extensions.Should().Equal(".acme");
        opts.StartMenu!.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Absent_options_block_leaves_options_null()
    {
        var result = ManifestParser.Parse("""
            spec: v1.0
            app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }
            build: { source: ./out }
            installer:
              scope: user
            """, "s.yaml");

        result.Manifest!.Installer!.Options.Should().BeNull();
    }

    // ── P10 (gap G11): app-defined custom components ─────────────────────────

    [Fact]
    public void Custom_component_parses_name_label_default_locked_when()
    {
        var opts = Parse("""
                components:
                  - name: sample_data
                    label: Install sample data
                    description: Copies a starter project
                    default: true
                    locked: false
                    when: param.edition == 'pro'
        """);

        opts.Components.Should().NotBeNull();
        var c = opts.Components!.Single();
        c.Name.Should().Be("sample_data");
        c.Label.English.Should().Be("Install sample data");
        c.Description!.English.Should().Be("Copies a starter project");
        c.Default.Should().BeTrue();
        c.Locked.Should().BeFalse();
        c.When.Should().Be("param.edition == 'pro'");
    }

    [Fact]
    public void Custom_component_label_accepts_localized_map()
    {
        var opts = Parse("""
                components:
                  - name: sample_data
                    label: { en: Sample data, de: Beispieldaten }
                    default: false
        """);

        var c = opts.Components!.Single();
        c.Label.Values["en"].Should().Be("Sample data");
        c.Label.Values["de"].Should().Be("Beispieldaten");
        c.Default.Should().BeFalse();
        c.Locked.Should().BeFalse("locked defaults to false when omitted");
    }

    [Fact]
    public void Custom_components_preserve_declared_order()
    {
        var opts = Parse("""
                components:
                  - name: bravo
                    label: B
                  - name: alpha
                    label: A
        """);

        opts.Components!.Select(c => c.Name).Should().Equal("bravo", "alpha");
    }

    [Fact]
    public void Custom_and_builtin_components_coexist()
    {
        var opts = Parse("""
                desktop_shortcut: true
                components:
                  - name: sample_data
                    label: Sample data
        """);

        opts.DesktopShortcut!.Enabled.Should().BeTrue();
        opts.Components!.Single().Name.Should().Be("sample_data");
    }

    [Fact]
    public void Custom_component_with_bad_identifier_is_diagnosed()
    {
        var result = ManifestParser.Parse(Yaml("""
                components:
                  - name: "1bad-name"
                    label: X
        """), "s.yaml");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidCustomComponent && d.Message.Contains("1bad-name"));
    }

    [Fact]
    public void Custom_component_colliding_with_builtin_is_diagnosed()
    {
        var result = ManifestParser.Parse(Yaml("""
                components:
                  - name: desktop_shortcut
                    label: X
        """), "s.yaml");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidCustomComponent && d.Message.Contains("desktop_shortcut"));
    }

    [Fact]
    public void Custom_component_colliding_with_parameter_is_diagnosed()
    {
        var result = ManifestParser.Parse("""
            spec: v1.0
            app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }
            build: { source: ./out }
            parameters:
              edition: { type: string, default: pro }
            installer:
              options:
                components:
                  - name: edition
                    label: X
            """, "s.yaml");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidCustomComponent && d.Message.Contains("edition"));
    }

    [Fact]
    public void Duplicate_custom_component_name_is_diagnosed()
    {
        var result = ManifestParser.Parse(Yaml("""
                components:
                  - name: dupe
                    label: X
                  - name: dupe
                    label: Y
        """), "s.yaml");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidCustomComponent && d.Message.Contains("dupe"));
    }

    [Fact]
    public void Custom_component_without_label_is_diagnosed()
    {
        var result = ManifestParser.Parse(Yaml("""
                components:
                  - name: no_label
                    default: true
        """), "s.yaml");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidCustomComponent && d.Message.Contains("no_label"));
    }
}
