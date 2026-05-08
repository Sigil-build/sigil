namespace SigilBuild.Wrapper.Engine;

internal sealed class StepFailureException : System.Exception
{
    public string StepId { get; }

    public StepFailureException(string stepId, string? error)
        : base($"step '{stepId}' failed: {error ?? "unknown"}")
    {
        StepId = stepId;
    }

    public StepFailureException()
    {
        StepId = string.Empty;
    }

    public StepFailureException(string message)
        : base(message)
    {
        StepId = string.Empty;
    }

    public StepFailureException(string message, System.Exception innerException)
        : base(message, innerException)
    {
        StepId = string.Empty;
    }
}
