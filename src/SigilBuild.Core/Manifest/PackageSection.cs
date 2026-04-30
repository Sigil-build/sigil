using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

public enum PackageFormat { Msix, Zip }
public enum TargetArchitecture { X64, Arm64 }

public sealed record MsixOptions(
    string? Publisher,
    string? Logo,
    IReadOnlyList<string>? Capabilities);

public sealed record PackageSection(
    IReadOnlyList<PackageFormat> Formats,
    IReadOnlyList<TargetArchitecture> Architectures,
    MsixOptions? Msix);
