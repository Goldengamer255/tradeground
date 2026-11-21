using System.Globalization;

namespace Tradeground.Game;

public sealed record Clue(int DieIndex, int Value, string Text);

/// <summary>
/// The secret behind a round: hidden dice decide what the asset is finally worth.
/// Fully determined by (spec, seed), so any round can be replayed exactly.
/// </summary>
public sealed class HiddenValueRound
{
    private readonly HiddenValueSpec _spec;
    private readonly int[] _dice;
    private readonly List<int> _privateOrder;
    private readonly List<int> _publicOrder;
    private readonly List<Clue> _publicClues = [];
    private int _privateCluesGiven;

    public HiddenValueRound(HiddenValueSpec spec, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _spec = spec;

        var random = new DeterministicRandom(seed);
        _dice = new int[spec.DiceCount];
        for (var i = 0; i < _dice.Length; i++)
        {
            _dice[i] = random.Next(1, spec.DiceSides + 1);
        }

        _privateOrder = [.. Enumerable.Range(0, spec.DiceCount)];
        random.Shuffle(_privateOrder);
        _publicOrder = [.. Enumerable.Range(0, spec.DiceCount)];
        random.Shuffle(_publicOrder);
    }

    public long TrueValue => _dice.Sum() * _spec.ValuePerPip;

    /// <summary>What a player who knows nothing should expect the asset to be worth.</summary>
    public decimal ExpectedValueWithNoInformation => _spec.DiceCount * (_spec.DiceSides + 1) / 2m * _spec.ValuePerPip;

    public IReadOnlyList<Clue> PublicClues => _publicClues;

    public bool HasMorePublicClues => _publicClues.Count < _dice.Length;

    /// <summary>Hands out dice round-robin, so with more players than dice some players share a clue.</summary>
    public Clue NextPrivateClue()
    {
        var die = _privateOrder[_privateCluesGiven % _privateOrder.Count];
        _privateCluesGiven++;
        return MakeClue(die, _spec.PrivateClueTemplate);
    }

    public Clue? RevealNextPublicClue()
    {
        if (!HasMorePublicClues)
        {
            return null;
        }

        var clue = MakeClue(_publicOrder[_publicClues.Count], _spec.PublicClueTemplate);
        _publicClues.Add(clue);
        return clue;
    }

    private Clue MakeClue(int dieIndex, string template)
    {
        var value = _dice[dieIndex];
        var text = template
            .Replace("{index}", (dieIndex + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{value}", value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return new Clue(dieIndex, value, text);
    }
}
