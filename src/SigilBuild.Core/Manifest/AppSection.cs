namespace SigilBuild.Core.Manifest;

public sealed record AppSection(
    string Id,
    string Name,
    string Version,
    string Publisher,
    string? Description,
    string? Homepage);
