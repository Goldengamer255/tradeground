namespace Tradeground.Game;

public enum Audience
{
    /// <summary>Younger players: friendly goods, plain language, simple controls.</summary>
    Explorer,

    /// <summary>Teens and adults new to trading: real terminology and the full order book.</summary>
    Trader,
}

/// <summary>
/// A game mode is data, not code: a new lesson or scenario is a new JSON file.
/// </summary>
public sealed record GameMode(
    string Id,
    string Name,
    Audience Audience,
    string Description,
    AssetSpec Asset,
    long StartingCash,
    long MaxPosition,
    long MaxOrderQuantity,
    int RoundSeconds,
    HiddenValueSpec HiddenValue)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add("Id is required.");
        }

        if (StartingCash <= 0)
        {
            errors.Add("StartingCash must be positive.");
        }

        if (MaxPosition <= 0)
        {
            errors.Add("MaxPosition must be positive.");
        }

        if (MaxOrderQuantity <= 0 || MaxOrderQuantity > MaxPosition)
        {
            errors.Add("MaxOrderQuantity must be between 1 and MaxPosition.");
        }

        if (RoundSeconds <= 0)
        {
            errors.Add("RoundSeconds must be positive.");
        }

        if (HiddenValue.DiceCount <= 0 || HiddenValue.DiceSides < 2 || HiddenValue.ValuePerPip <= 0)
        {
            errors.Add("HiddenValue needs DiceCount >= 1, DiceSides >= 2 and ValuePerPip >= 1.");
        }

        return errors;
    }
}

public sealed record AssetSpec(string Symbol, string DisplayName, string Unit);

/// <summary>
/// The asset settles at (sum of hidden dice) x ValuePerPip. Each player privately sees one die,
/// and the host can reveal dice publicly during the round.
/// Templates use {index} (1-based die number) and {value}.
/// </summary>
public sealed record HiddenValueSpec(
    int DiceCount,
    int DiceSides,
    long ValuePerPip,
    string PrivateClueTemplate,
    string PublicClueTemplate);
