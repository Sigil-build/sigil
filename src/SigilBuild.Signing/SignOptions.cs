namespace SigilBuild.Signing;

public sealed record SignOptions(
    string ArtifactPath,
    bool ProduceDetachedSignature);
