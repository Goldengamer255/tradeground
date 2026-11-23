using System.Collections.Concurrent;
using Tradeground.Api.Rooms;
using Tradeground.Game.Charts;

namespace Tradeground.Api.Charts;

/// <summary>
/// Holds solo Chart Replay runs in memory, keyed by a random secret.
/// </summary>
/// <remarks>
/// A solo run has exactly one player, so a simple lock per run is enough. The multiplayer
/// rooms need the heavier actor model because many players hit one room at once.
/// Idle runs are swept away so memory can't grow forever.
/// </remarks>
public sealed class ChartRunStore(ChartLibrary library, TimeProvider time)
{
    public const int MaxRuns = 10_000;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, Entry> _runs = new(StringComparer.Ordinal);

    public ChartLibrary Library => library;

    /// <summary>Starts a new run, or returns null if the server is at capacity.</summary>
    public (string Key, ChartRun Run)? Start(ulong seed)
    {
        SweepIdle();
        if (_runs.Count >= MaxRuns)
        {
            return null;
        }

        var key = Secrets.NewToken();
        var run = new ChartRun(library, seed);
        _runs[key] = new Entry(run, time.GetUtcNow());
        return (key, run);
    }

    /// <summary>Runs <paramref name="work"/> on the run while holding its lock. Null if the key is unknown.</summary>
    public T? Use<T>(string? key, Func<ChartRun, T> work)
        where T : class
    {
        if (string.IsNullOrEmpty(key) || !_runs.TryGetValue(key, out var entry))
        {
            return null;
        }

        lock (entry)
        {
            entry.LastUsed = time.GetUtcNow();
            return work(entry.Run);
        }
    }

    private void SweepIdle()
    {
        var cutoff = time.GetUtcNow() - IdleTimeout;
        foreach (var (key, entry) in _runs)
        {
            if (entry.LastUsed < cutoff)
            {
                _runs.TryRemove(key, out _);
            }
        }
    }

    private sealed class Entry(ChartRun run, DateTimeOffset lastUsed)
    {
        public ChartRun Run { get; } = run;
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }
}
