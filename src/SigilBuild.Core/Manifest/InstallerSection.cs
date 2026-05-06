namespace SigilBuild.Core.Manifest;

public sealed record InstallerBrand(
    string? Logo,
    string? Hero,
    string? PrimaryColor,
    string? AccentColor,
    string? GradientStart,
    string? GradientMid,
    string? GradientEnd);

public sealed record InstallerSection(InstallerBrand? Brand);
