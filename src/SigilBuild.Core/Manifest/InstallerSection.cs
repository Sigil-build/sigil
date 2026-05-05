namespace SigilBuild.Core.Manifest;

public sealed record InstallerBrand(
    string? Logo,
    string? Hero,
    string? PrimaryColor,
    string? AccentColor);

public sealed record InstallerSection(InstallerBrand? Brand);
