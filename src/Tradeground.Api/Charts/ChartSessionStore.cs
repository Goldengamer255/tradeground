using System.Collections.Concurrent;
using Tradeground.Api.Rooms;
using Tradeground.Game.Charts;

namespace Tradeground.Api.Charts;

/// <summary>
/// Holds solo Chart Replay games in memory, keyed by a random secret.
/// </summary>
/// <remarks>
/// A solo game has exactly one player, so a simple lock per session is enough. The multiplayer
/// rooms need the heavier actor model because many players hit one room at once.
/// Idle sessions are swept away so memory can't grow forever.
/// </remarks>
public sealed class ChartSessionStore(ChartLibrary library, TimeProvider time)
{
    public const int MaxSessions = 10_000;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    /// <summary>Starts a new game, or returns null if the server is at capacity.</summary>
    public (string Key, ChartSession Session)? Start(ulong seed)
    {
        SweepIdle();
        if (_sessions.Count >= MaxSessions)
        {
            return null;
        }

        var key = Secrets.NewToken();
        var session = new ChartSession(library, seed);
        _sessions[key] = new Entry(session, time.GetUtcNow());
        return (key, session);
    }

    /// <summary>Runs <paramref name="work"/> on the session while holding its lock. Null if the key is unknown.</summary>
    public T? Use<T>(string? key, Func<ChartSession, T> work)
        where T : class
    {
        if (string.IsNullOrEmpty(key) || !_sessions.TryGetValue(key, out var entry))
        {
            return null;
        }

        lock (entry)
        {
            entry.LastUsed = time.GetUtcNow();
            return work(entry.Session);
        }
    }

    private void SweepIdle()
    {
        var cutoff = time.GetUtcNow() - IdleTimeout;
        foreach (var (key, entry) in _sessions)
        {
            if (entry.LastUsed < cutoff)
            {
                _sessions.TryRemove(key, out _);
            }
        }
    }

    private sealed class Entry(ChartSession session, DateTimeOffset lastUsed)
    {
        public ChartSession Session { get; } = session;
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }
}
