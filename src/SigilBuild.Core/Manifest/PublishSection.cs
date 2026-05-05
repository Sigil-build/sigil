namespace SigilBuild.Core.Manifest;

public sealed record GitHubPublishConfig(string Repo, string TagPrefix, bool Draft);

public sealed record PublishSection(GitHubPublishConfig? GitHub);
