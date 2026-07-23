using SigilBuild.Core.Manifest;

namespace SigilBuild.Packaging;

/// <summary>
/// How the <c>exe</c> format's payload is delivered (P12 / T12.5).
/// </summary>
public enum PayloadMode
{
    /// <summary>The app payload is embedded directly in the stamped Setup.exe (the original, unchanged behavior).</summary>
    Embedded,

    /// <summary>
    /// The stamped Setup.exe carries NO app payload. Instead the packager emits
    /// the normal embedded package (hosted at <see cref="PackOptions.PackageUrl"/>)
    /// PLUS a small stub whose only install action is: <c>http_download</c> that
    /// package (sha256-verified) to a temp location, then run it.
    /// </summary>
    Web,
}

public sealed record PackOptions(
    string SourceDirectory,
    string OutputDirectory,
    PackageFormat Format,
    TargetArchitecture Architecture,
    PayloadMode Payload = PayloadMode.Embedded,
    string? PackageUrl = null);
