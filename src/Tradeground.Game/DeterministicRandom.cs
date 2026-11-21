namespace Tradeground.Game;

/// <summary>
/// SplitMix64: a tiny, fast pseudo-random generator whose output is fixed forever for a given seed.
/// </summary>
/// <remarks>
/// System.Random's seeded output is not guaranteed to stay the same across .NET versions.
/// Daily Challenges and replays must produce the exact same market from the same seed,
/// so the algorithm is owned here instead.
/// </remarks>
public sealed class DeterministicRandom(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
    public int Next(int minInclusive, int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minInclusive, maxExclusive);
        var range = (ulong)((long)maxExclusive - minInclusive);

        // Rejection sampling removes modulo bias.
        var limit = ulong.MaxValue - (ulong.MaxValue % range);
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value >= limit);

        return (int)((long)minInclusive + (long)(value % range));
    }

    /// <summary>Fisher-Yates shuffle.</summary>
    public void Shuffle<T>(IList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Next(0, i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
