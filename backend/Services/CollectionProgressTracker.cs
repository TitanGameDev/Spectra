using System.Collections.Concurrent;

namespace Spectra.Api.Services;

public record CollectionProgressLine(int Seq, DateTimeOffset At, string Message);

// Buffers a live line-by-line progress feed for an in-flight
// CustomerCollectionService.CollectAsync run, keyed by customer ID, so
// Settings can poll GET /api/customers/{id}/collect/progress while the
// (synchronous, blocking) collect request is still in flight and render a
// terminal-style feed instead of just a spinner for the length of the run.
// Registered Singleton — has to be the same instance across the request
// that's running collection and the separate requests polling it, same
// reasoning as CollectionLockRegistry.
//
// The AsyncLocal is how GraphRetryHandler (a generic DelegatingHandler with
// no idea which customer's collection triggered a given Graph call) reports
// "waiting on a 429" into the right session — CollectAsync sets it for the
// duration of its own async call tree via Begin(), and it flows through
// Parallel.ForEachAsync's per-item tasks via ExecutionContext the same as
// any other AsyncLocal.
public class CollectionProgressTracker
{
    private const int MaxBufferedLines = 1000;

    private static readonly AsyncLocal<Action<string>?> Ambient = new();

    private readonly ConcurrentDictionary<int, Session> sessions = new();

    private class Session
    {
        public readonly List<CollectionProgressLine> Lines = [];
        public bool IsRunning = true;
        public int NextSeq = 1;
    }

    public IDisposable Begin(int customerId)
    {
        var session = new Session();
        sessions[customerId] = session;
        Ambient.Value = message => Append(session, message);
        return new Scope(() =>
        {
            session.IsRunning = false;
            Ambient.Value = null;
        });
    }

    // No-ops outside of a Begin() scope (e.g. Graph calls made for reasons
    // other than a tracked collection run) — callers don't need to check.
    public void Report(string message) => Ambient.Value?.Invoke(message);

    public (bool IsRunning, List<CollectionProgressLine> Lines) GetSince(int customerId, int afterSeq)
    {
        if (!sessions.TryGetValue(customerId, out var session))
        {
            return (false, []);
        }
        lock (session.Lines)
        {
            return (session.IsRunning, session.Lines.Where(l => l.Seq > afterSeq).ToList());
        }
    }

    private static void Append(Session session, string message)
    {
        lock (session.Lines)
        {
            session.Lines.Add(new CollectionProgressLine(session.NextSeq++, DateTimeOffset.UtcNow, message));
            if (session.Lines.Count > MaxBufferedLines)
            {
                session.Lines.RemoveRange(0, session.Lines.Count - MaxBufferedLines);
            }
        }
    }

    private class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
