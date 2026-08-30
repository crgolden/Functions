namespace Functions.Curator.Jobs;

public sealed class JobRunStoodDownException : Exception
{
    private JobRunStoodDownException(string message, string runId)
        : base(message) => RunId = runId;

    public string RunId { get; }

    public static JobRunStoodDownException ForRun(string runId) =>
        new($"Run {runId} left the running state while the worker was still processing it.", runId);
}
