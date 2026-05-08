namespace SigilBuild.Wrapper.Engine;

public sealed record StepResult(bool Success, string? Error)
{
    public static StepResult Ok() => new(true, null);
    public static StepResult Failed(string error) => new(false, error);
}
