namespace Functions.Curator.Jobs;

using JetBrains.Annotations;

[PublicAPI]
public sealed class JobRunStoodDownException : Exception
{
    private JobRunStoodDownException(string message, Guid runId)
        : base(message) => RunId = runId;

    public Guid RunId { get; }

    public static JobRunStoodDownException ForRun(Guid runId) =>
        new($"Run {runId} left the running state while the worker was still processing it.", runId);
}
