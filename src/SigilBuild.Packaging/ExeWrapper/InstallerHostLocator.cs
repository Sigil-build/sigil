using System;
using System.IO;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Locates the AOT-published <c>installer.exe</c> (the Avalonia wizard host
/// from <c>SigilBuild.Installer.Host</c>) that <see cref="ExeWrapperPackager"/>
/// bundles into the produced setup.exe when the manifest declares an
/// <c>installer:</c> block. Mirrors <see cref="WrapperRuntimeLocator"/> for the
/// wrapper runtime — same lookup convention, same "publish + copy into
/// runtimes/win-x64/" build-time wiring.
/// </summary>
internal static class InstallerHostLocator
{
    /// <summary>
    /// Returns the path to the staged installer host, or <c>null</c> when not
    /// found. ExeWrapperPackager treats missing host as "skip bundling, emit a
    /// diagnostic" rather than a hard failure — the wrapper still runs the
    /// install_steps in /S mode without a UI.
    /// </summary>
    public static string? TryLocate()
    {
        var sdkRoot = AppContext.BaseDirectory;
        var candidate = Path.Combine(sdkRoot, "runtimes", "win-x64", "installer.exe");
        return File.Exists(candidate) ? candidate : null;
    }
}
