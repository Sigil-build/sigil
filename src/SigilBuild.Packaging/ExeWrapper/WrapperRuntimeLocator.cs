using System;
using System.IO;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Locates the Native-AOT-published <c>SigilBuild.Installer.Host.exe</c> runtime
/// that <see cref="ExeWrapperPackager"/> stamps with a step blob and payload.
/// </summary>
/// <remarks>
/// The runtime is supplied by build-time wiring (spec T3): the
/// <c>scripts/publish-installer-runtime.ps1</c> script (invoked by CI and local
/// pack) runs
/// <c>dotnet publish src/SigilBuild.Installer.Host -c Release -r &lt;rid&gt; -p:PublishAot=true</c>
/// for each declared architecture and copies the produced <c>installer.exe</c> into
/// <c>runtimes/&lt;rid&gt;/SigilBuild.Installer.Host.exe</c> next to the packaging /
/// CLI output. Because the manifest may declare
/// <c>architectures: [x64, arm64]</c>, the runtime is resolved per target
/// architecture and the packager produces one <c>-Setup.exe</c> per architecture.
/// </remarks>
internal static class WrapperRuntimeLocator
{
    /// <summary>The file name of the stamped runtime staged under each RID folder.</summary>
    internal const string RuntimeFileName = "SigilBuild.Installer.Host.exe";

    /// <summary>
    /// Maps a <see cref="TargetArchitecture"/> to its .NET runtime identifier
    /// (RID) folder name — the same folder the publish script stages into.
    /// </summary>
    internal static string RidFor(TargetArchitecture architecture) => architecture switch
    {
        TargetArchitecture.X64 => "win-x64",
        TargetArchitecture.Arm64 => "win-arm64",
        _ => throw new ArgumentOutOfRangeException(
            nameof(architecture), architecture, "Unsupported target architecture for the exe wrapper runtime."),
    };

    /// <summary>
    /// Resolves the staged AOT host runtime for <paramref name="architecture"/>,
    /// relative to <paramref name="baseDirectory"/> (defaults to the SDK/CLI base
    /// directory). Throws <see cref="FileNotFoundException"/> when the runtime has
    /// not been published and staged.
    /// </summary>
    public static string Locate(TargetArchitecture architecture, string? baseDirectory = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var rid = RidFor(architecture);
        var candidate = Path.Combine(root, "runtimes", rid, RuntimeFileName);
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                $"{RuntimeFileName} for {rid} not found at {candidate}; the Native AOT runtime " +
                "must be published and staged into the SDK package before pack time. Run " +
                "scripts/publish-installer-runtime.ps1 (or the CI aot-publish job).",
                candidate);
        }
        return candidate;
    }
}
