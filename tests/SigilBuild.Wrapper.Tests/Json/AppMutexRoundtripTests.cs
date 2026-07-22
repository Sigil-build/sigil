using System;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Json;

/// <summary>P6: installer.app_mutex survives the blob wire form (M0 discipline).</summary>
public class AppMutexRoundtripTests
{
    private static WrapperBlob Blob(string[]? appMutex) => new(
        AppId: "com.acme.Studio",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        AppMutex: appMutex);

    [Fact]
    public void AppMutex_roundtrips_through_the_blob()
    {
        var blob = Blob(new[] { @"Global\AcmeStudio", @"Local\AcmeHelper" });

        var back = SerializableWrapperBlob.ToWrapperBlob(SerializableWrapperBlob.FromWrapperBlob(blob));

        back.AppMutex.Should().Equal(@"Global\AcmeStudio", @"Local\AcmeHelper");
    }

    [Fact]
    public void No_app_mutex_roundtrips_to_null()
        => SerializableWrapperBlob.ToWrapperBlob(SerializableWrapperBlob.FromWrapperBlob(Blob(null)))
            .AppMutex.Should().BeNull();
}
