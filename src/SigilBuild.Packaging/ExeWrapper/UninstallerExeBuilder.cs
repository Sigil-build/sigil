using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Pack-time builder for the lightweight <c>uninstaller.exe</c>. Copies the
/// AOT-published wrapper runtime to a temp file, stamps it with an
/// uninstall-only blob (steps from <c>manifest.Uninstall</c>, IsUninstaller=true,
/// empty payload, no installer host), and returns the path. The caller embeds
/// the bytes as <c>SIGIL_UNINSTALLER_V1</c> inside the main setup.exe.
/// </summary>
internal static class UninstallerExeBuilder
{
    public static async Task<string> BuildAsync(
        string wrapperRuntimePath,
        string appId,
        AppMetadata app,
        IReadOnlyList<InstallStep> uninstallSteps,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wrapperRuntimePath);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(uninstallSteps);

        var outputPath = Path.Combine(Path.GetTempPath(),
            $"sigil-uninstaller-{Guid.NewGuid():N}.exe");
        File.Copy(wrapperRuntimePath, outputPath, overwrite: true);

        var blob = new WrapperBlob(
            AppId: appId,
            App: app,
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: uninstallSteps,
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Uninstall: Array.Empty<InstallStep>(),
            IsUninstaller: true);

        var serializable = SerializableWrapperBlob.FromWrapperBlob(blob);
        var json = JsonSerializer.Serialize(
            serializable, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        var blobBytes = Encoding.UTF8.GetBytes(json);
        // SIGIL_PAYLOAD_V1 must be present (Win32 UpdateResource rejects
        // zero-length data — it interprets that as "delete the resource", and
        // the freshly-copied wrapper has no payload resource yet to delete).
        // Uninstall flow never extracts the payload, so a single sentinel byte
        // is sufficient.
        var payloadBytes = new byte[] { 0 };

        await WrapperResourceWriter.WriteAsync(
            outputPath, blobBytes, payloadBytes, installerHostBundle: null, ct)
            .ConfigureAwait(false);

        return outputPath;
    }
}
