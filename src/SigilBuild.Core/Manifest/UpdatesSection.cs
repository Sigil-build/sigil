namespace SigilBuild.Core.Manifest;

public sealed record UpdatesSection(
    string Channel,
    string? ManifestUrl,
    int DeltaTargets,
    string? SigningKey);
