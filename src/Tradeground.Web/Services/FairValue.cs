using Tradeground.Contracts;

namespace Tradeground.Web.Services;

/// <summary>A fair-value estimate and the full range the hidden value could still be in.</summary>
public sealed record Estimate(decimal Value, long Low, long High, int KnownCount);

/// <summary>
/// The lesson of Market Maker: price = what you know + the average of what you don't.
/// Pure maths, so it's unit tested.
/// </summary>
public static class FairValue
{
    /// <param name="known">Die number (1-based) to value, for every die this player can see.</param>
    public static Estimate Of(ModeSummary mode, IReadOnlyDictionary<int, int> known)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(known);

        long sum = known.Values.Sum();
        var unknown = mode.DiceCount - known.Count;
        var averageRoll = (mode.DiceSides + 1) / 2m;

        return new Estimate(
            (sum + (unknown * averageRoll)) * mode.ValuePerPip,
            (sum + unknown) * mode.ValuePerPip,
            (sum + ((long)unknown * mode.DiceSides)) * mode.ValuePerPip,
            known.Count);
    }

    /// <summary>Public news plus the player's own private die.</summary>
    public static Dictionary<int, int> Known(IEnumerable<ClueView> publicClues, ClueView? privateClue)
    {
        var known = publicClues.ToDictionary(c => c.Die, c => c.Value);
        if (privateClue is not null)
        {
            known[privateClue.Die] = privateClue.Value;
        }

        return known;
    }
}
