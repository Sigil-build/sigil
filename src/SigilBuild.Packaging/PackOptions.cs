using SigilBuild.Core.Manifest;

namespace SigilBuild.Packaging;

public sealed record PackOptions(
    string SourceDirectory,
    string OutputDirectory,
    PackageFormat Format,
    TargetArchitecture Architecture);
