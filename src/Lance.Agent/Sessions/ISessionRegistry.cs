using System.Collections.Concurrent;

namespace Lance.Agent.Sessions;

// In-memory registry of live sessions. Enforces global id uniqueness on add (a
// colliding `lance connect` is refused) and the Provisioned → Connected → Ended
// state machine. Thread-safe: the detection loop, the connect handshake, and
// reconciliation all touch it concurrently.
internal interface ISessionRegistry
{
    bool TryAdd(Session session);
    bool TryGet(string id, out Session? session);
    IReadOnlyList<Session> GetAll();
    bool TryMarkConnected(string id, DateTimeOffset connectedAt);
    bool TryMarkEnded(string id);
    void Remove(string id);
}

internal sealed class SessionRegistry : ISessionRegistry
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public bool TryAdd(Session session)
    {
        return _sessions.TryAdd(session.Id, session);
    }

    public bool TryGet(string id, out Session? session)
    {
        return _sessions.TryGetValue(id, out session);
    }

    public IReadOnlyList<Session> GetAll()
    {
        List<Session> result = [];
        foreach (KeyValuePair<string, Session> pair in _sessions)
        {
            result.Add(pair.Value);
        }

        return result;
    }

    public bool TryMarkConnected(string id, DateTimeOffset connectedAt)
    {
        return TryTransition(id, current =>
        {
            if (current.State != SessionState.Provisioned)
            {
                return null;   // already connected or ended — not a valid transition
            }

            return current with { State = SessionState.Connected, ConnectedAt = connectedAt };
        });
    }

    public bool TryMarkEnded(string id)
    {
        return TryTransition(id, current =>
        {
            if (current.State == SessionState.Ended)
            {
                return null;
            }

            return current with { State = SessionState.Ended };
        });
    }

    public void Remove(string id)
    {
        _sessions.TryRemove(id, out _);
    }

    private bool TryTransition(string id, Func<Session, Session?> transition)
    {
        // Compare-and-swap loop: re-read and retry if another thread updated the entry
        // between our read and write, so concurrent transitions never clobber each other.
        while (_sessions.TryGetValue(id, out Session? current))
        {
            Session? next = transition(current!);
            if (next is null)
            {
                return false;
            }

            if (_sessions.TryUpdate(id, next, current!))
            {
                return true;
            }
        }

        return false;
    }
}
