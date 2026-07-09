namespace SigilBuild.Wrapper.Engine;

public sealed record EngineResult(bool Success, RollbackJournal Journal, string? Error)
{
    public static EngineResult Ok(RollbackJournal j) => new(true, j, null);
    public static EngineResult Failed(RollbackJournal j, string error) => new(false, j, error);
}
