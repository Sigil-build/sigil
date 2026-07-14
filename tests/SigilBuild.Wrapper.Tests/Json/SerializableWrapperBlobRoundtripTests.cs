using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;

namespace SigilBuild.Wrapper.Tests.Json;

/// <summary>
/// Round-trip tests for the M0 manifest/blob data surface additions to
/// <see cref="SerializableWrapperBlob"/> — brand token maps, base64
/// logo/hero, license text, ARP metadata, scope, and declared screens.
/// Exercises the source-generated <see cref="WrapperBlobJsonContext"/> to
/// prove every new type serializes without reflection (Native AOT gate).
/// </summary>
public class SerializableWrapperBlobRoundtripTests
{
    private static SerializableWrapperBlob RoundTrip(SerializableWrapperBlob blob)
    {
        var json = JsonSerializer.Serialize(
            blob, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        var bytes = Encoding.UTF8.GetBytes(json);
        var back = JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(bytes),
            WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        back.Should().NotBeNull();
        return back!;
    }

    [Fact]
    public void Defaults_roundtrip_cleanly()
    {
        var back = RoundTrip(new SerializableWrapperBlob());

        back.DisplayName.Should().BeNull();
        back.Version.Should().BeNull();
        back.Publisher.Should().BeNull();
        back.EstimatedSizeBytes.Should().BeNull();
        back.Scope.Should().Be(InstallScope.Auto);
        back.BrandTokensLight.Should().BeNull();
        back.BrandTokensDark.Should().BeNull();
        back.LogoBase64.Should().BeNull();
        back.HeroBase64.Should().BeNull();
        back.LicenseText.Should().BeNull();
        back.Screens.Should().BeEmpty();
        back.AppName.Should().BeNull();
        back.InstallDir.Should().BeNull();
        back.SignDeclared.Should().BeFalse();
    }

    [Fact]
    public void App_name_and_install_dir_override_roundtrip()
    {
        // T13: the {install_dir} template + App.Name travel in the blob and survive
        // the WrapperBlob <-> SerializableWrapperBlob conversion both ways.
        var blob = new WrapperBlob(
            AppId: "com.acme.Studio",
            Parameters: System.Array.Empty<ParameterDefinition>(),
            InstallSteps: System.Array.Empty<InstallStep>(),
            PreInstall: System.Array.Empty<InstallStep>(),
            PostInstall: System.Array.Empty<InstallStep>(),
            UpdateSteps: System.Array.Empty<InstallStep>(),
            Scope: InstallScope.Auto,
            Options: null,
            AppName: "Acme Studio",
            InstallDir: "{scope_root}/Acme Studio");

        var back = RoundTrip(SerializableWrapperBlob.FromWrapperBlob(blob));

        back.AppName.Should().Be("Acme Studio");
        back.InstallDir.Should().Be("{scope_root}/Acme Studio");

        var reconstructed = SerializableWrapperBlob.ToWrapperBlob(back);
        reconstructed.AppName.Should().Be("Acme Studio");
        reconstructed.InstallDir.Should().Be("{scope_root}/Acme Studio");
    }

    [Fact]
    public void SignDeclared_roundtrips()
    {
        RoundTrip(new SerializableWrapperBlob { SignDeclared = true }).SignDeclared.Should().BeTrue();
        RoundTrip(new SerializableWrapperBlob { SignDeclared = false }).SignDeclared.Should().BeFalse();
    }

    [Fact]
    public void Arp_metadata_and_scope_roundtrip()
    {
        var back = RoundTrip(new SerializableWrapperBlob
        {
            AppId = "com.acme.Studio",
            DisplayName = "Acme Studio",
            Version = "3.2.0",
            Publisher = "Acme, Inc.",
            EstimatedSizeBytes = 123_456_789L,
            Scope = InstallScope.Machine,
        });

        back.AppId.Should().Be("com.acme.Studio");
        back.DisplayName.Should().Be("Acme Studio");
        back.Version.Should().Be("3.2.0");
        back.Publisher.Should().Be("Acme, Inc.");
        back.EstimatedSizeBytes.Should().Be(123_456_789L);
        back.Scope.Should().Be(InstallScope.Machine);
    }

    [Fact]
    public void Arp_fields_roundtrip_through_the_in_memory_WrapperBlob()
    {
        // T10: the real ARP fields must survive WrapperBlob -> Serializable -> wire ->
        // Serializable -> WrapperBlob so the runtime's PersistCompletion reads them.
        var blob = new WrapperBlob(
            AppId: "com.acme.Studio",
            Parameters: System.Array.Empty<ParameterDefinition>(),
            InstallSteps: System.Array.Empty<InstallStep>(),
            PreInstall: System.Array.Empty<InstallStep>(),
            PostInstall: System.Array.Empty<InstallStep>(),
            UpdateSteps: System.Array.Empty<InstallStep>(),
            DisplayName: "Acme Studio",
            Publisher: "Acme, Inc.",
            Version: "3.2.0",
            EstimatedSizeBytes: 123_456_789L);

        var reconstructed = SerializableWrapperBlob.ToWrapperBlob(
            RoundTrip(SerializableWrapperBlob.FromWrapperBlob(blob)));

        reconstructed.DisplayName.Should().Be("Acme Studio");
        reconstructed.Publisher.Should().Be("Acme, Inc.");
        reconstructed.Version.Should().Be("3.2.0");
        reconstructed.EstimatedSizeBytes.Should().Be(123_456_789L);
    }

    [Fact]
    public void Installer_vars_roundtrip_in_declaration_order()
    {
        // P1: installer.vars survive WrapperBlob -> Serializable -> wire ->
        // Serializable -> WrapperBlob, preserving declaration order and expressions.
        var blob = new WrapperBlob(
            AppId: "com.acme.Studio",
            Parameters: System.Array.Empty<ParameterDefinition>(),
            InstallSteps: System.Array.Empty<InstallStep>(),
            PreInstall: System.Array.Empty<InstallStep>(),
            PostInstall: System.Array.Empty<InstallStep>(),
            UpdateSteps: System.Array.Empty<InstallStep>(),
            Vars: new[]
            {
                new InstallerVar("old_path", "registry_read('HKLM', 'Software\\Acme', 'Path')"),
                new InstallerVar("is_upgrade", "installed_version(app.id) != ''"),
            });

        var wire = RoundTrip(SerializableWrapperBlob.FromWrapperBlob(blob));
        wire.Vars.Should().HaveCount(2);
        wire.Vars[0].Name.Should().Be("old_path");
        wire.Vars[0].Expression.Should().Be(@"registry_read('HKLM', 'Software\Acme', 'Path')");
        wire.Vars[1].Name.Should().Be("is_upgrade");

        var reconstructed = SerializableWrapperBlob.ToWrapperBlob(wire);
        reconstructed.Vars.Should().NotBeNull();
        reconstructed.Vars!.Should().HaveCount(2);
        reconstructed.Vars![0].Name.Should().Be("old_path");
        reconstructed.Vars![1].Expression.Should().Contain("installed_version");
    }

    [Fact]
    public void No_vars_roundtrips_to_empty()
    {
        RoundTrip(new SerializableWrapperBlob()).Vars.Should().BeEmpty();
    }

    [Fact]
    public void Brand_token_maps_and_assets_roundtrip()
    {
        var back = RoundTrip(new SerializableWrapperBlob
        {
            BrandTokensLight = new Dictionary<string, string>
            {
                ["accent"] = "#4F46E5",
                ["railBg"] = "#312E81",
            },
            BrandTokensDark = new Dictionary<string, string>
            {
                ["accent"] = "#6366F1",
                ["railBg"] = "#1E1B4B",
            },
            LogoBase64 = "aGVsbG8tbG9nbw==",
            HeroBase64 = "aGVsbG8taGVybw==",
        });

        back.BrandTokensLight.Should().NotBeNull();
        back.BrandTokensLight!.Should().HaveCount(2);
        back.BrandTokensLight!["accent"].Should().Be("#4F46E5");
        back.BrandTokensLight!["railBg"].Should().Be("#312E81");
        back.BrandTokensDark.Should().NotBeNull();
        back.BrandTokensDark!["accent"].Should().Be("#6366F1");
        back.LogoBase64.Should().Be("aGVsbG8tbG9nbw==");
        back.HeroBase64.Should().Be("aGVsbG8taGVybw==");
    }

    [Fact]
    public void License_text_roundtrips()
    {
        const string license = "Copyright (c) Acme.\nAll rights reserved.\n";
        var back = RoundTrip(new SerializableWrapperBlob { LicenseText = license });

        back.LicenseText.Should().Be(license);
    }

    [Fact]
    public void Declared_screens_roundtrip()
    {
        var back = RoundTrip(new SerializableWrapperBlob
        {
            Screens = new[]
            {
                new SerializableInstallerScreen
                {
                    Id = "configure",
                    Title = "Configure {app.name}",
                    Subtitle = "Connect to your server and set preferences.",
                    When = "param.autostart == true",
                    Fields = new[]
                    {
                        new SerializableScreenField { Param = "server_address" },
                        new SerializableScreenField { Param = "channel", Widget = "radio" },
                    },
                },
            },
        });

        back.Screens.Should().HaveCount(1);
        var screen = back.Screens[0];
        screen.Id.Should().Be("configure");
        screen.Title.Should().Be("Configure {app.name}");
        screen.Subtitle.Should().Be("Connect to your server and set preferences.");
        screen.When.Should().Be("param.autostart == true");
        screen.Fields.Should().HaveCount(2);
        screen.Fields[0].Param.Should().Be("server_address");
        screen.Fields[0].Widget.Should().BeNull();
        screen.Fields[1].Param.Should().Be("channel");
        screen.Fields[1].Widget.Should().Be("radio");
    }

    [Fact]
    public void Screen_converters_preserve_shape()
    {
        var core = new InstallerScreen(
            Id: "configure",
            Title: "Configure {app.name}",
            Subtitle: null,
            When: "param.autostart == true",
            Fields: new[]
            {
                new ScreenField("server_address", null),
                new ScreenField("channel", "radio"),
            });

        var wire = SerializableInstallerScreen.FromInstallerScreen(core);
        var back = SerializableInstallerScreen.ToInstallerScreen(RoundTrip(
            new SerializableWrapperBlob { Screens = new[] { wire } }).Screens[0]);

        back.Id.Should().Be(core.Id);
        back.Title.Should().Be(core.Title);
        back.Subtitle.Should().BeNull();
        back.When.Should().Be(core.When);
        back.Fields.Should().HaveCount(2);
        back.Fields[0].Param.Should().Be("server_address");
        back.Fields[0].Widget.Should().BeNull();
        back.Fields[1].Param.Should().Be("channel");
        back.Fields[1].Widget.Should().Be("radio");
    }
}
