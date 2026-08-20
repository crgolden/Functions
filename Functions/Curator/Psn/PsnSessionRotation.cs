namespace Functions.Curator.Psn;

using System.Diagnostics;
using System.Runtime.ExceptionServices;

public sealed class PsnSessionRotation
{
    private const string SessionRotatedEvent = "curator.psn.session-rotated";

    private readonly IReadOnlyList<PsnSession> _sessions;

    private int _index;

    public PsnSessionRotation(IReadOnlyList<PsnSession> sessions)
    {
        if (sessions.Count == 0)
        {
            throw new ArgumentException("PsnSessionRotation requires at least one session.", nameof(sessions));
        }

        _sessions = sessions;
    }

    public int Count => _sessions.Count;

    public async Task<T> RunAsync<T>(Func<PsnSession, Task<T>> operation)
    {
        ExceptionDispatchInfo? lastRejection = null;
        for (var attempt = 0; attempt < _sessions.Count; attempt++)
        {
            var rejectedIndex = _index;
            try
            {
                return await operation(_sessions[_index]).ConfigureAwait(false);
            }
            catch (PsnAuthException error)
            {
                lastRejection = ExceptionDispatchInfo.Capture(error);
                Telemetry.Metrics.PsnSessionRotated();
                Telemetry.Tracing.RecordHandledException(SessionRotatedEvent, error, new ActivityTagsCollection
                {
                    { "account.index", rejectedIndex },
                    { "account.count", _sessions.Count },
                });
                _index = (_index + 1) % _sessions.Count;
            }
        }

        lastRejection?.Throw();
        throw new PsnAuthException("Every configured PSN account rejected the request.");
    }
}
