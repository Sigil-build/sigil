using System;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Json;

/// <summary>
/// P12 (T12.3): the app manifest's <c>updates:</c> metadata (manifestUrl /
/// signingKey / channel) survives the blob wire form so the <c>/Update</c>
/// runtime can read it back (M0 lockstep discipline). Plain strings — no new
/// source-gen registration needed on <see cref="WrapperBlobJsonContext"/>.
/// </summary>
public class UpdatesMetadataRoundtripTests
{
    private static SerializableWrapperBlob RoundTrip(SerializableWrapperBlob blob)
    {
        var json = JsonSerializer.Serialize(
            blob, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        var back = JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(json)),
            WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        back.Should().NotBeNull();
        return back!;
    }

    private static WrapperBlob Blob(string? manifestUrl, string? signingKey, string? channel) => new(
        AppId: "com.acme.Studio",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        UpdateManifestUrl: manifestUrl,
        UpdateSigningKey: signingKey,
        UpdateChannel: channel);

    [Fact]
    public void Updates_metadata_roundtrips_through_the_blob()
    {
        var blob = Blob(
            "https://updates.acme.com/studio/stable.json",
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE",
            "stable");

        var reconstructed = SerializableWrapperBlob.ToWrapperBlob(
            RoundTrip(SerializableWrapperBlob.FromWrapperBlob(blob)));

        reconstructed.UpdateManifestUrl.Should().Be("https://updates.acme.com/studio/stable.json");
        reconstructed.UpdateSigningKey.Should().Be("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE");
        reconstructed.UpdateChannel.Should().Be("stable");
    }

    [Fact]
    public void No_updates_block_roundtrips_to_nulls()
    {
        var reconstructed = SerializableWrapperBlob.ToWrapperBlob(
            RoundTrip(SerializableWrapperBlob.FromWrapperBlob(Blob(null, null, null))));

        reconstructed.UpdateManifestUrl.Should().BeNull();
        reconstructed.UpdateSigningKey.Should().BeNull();
        reconstructed.UpdateChannel.Should().BeNull();
    }

    [Fact]
    public void Default_dto_has_null_updates_metadata()
    {
        var back = RoundTrip(new SerializableWrapperBlob());

        back.ManifestUrl.Should().BeNull();
        back.SigningKey.Should().BeNull();
        back.Channel.Should().BeNull();
    }
}
