using Tradeground.Contracts;
using Tradeground.Engine;
using Tradeground.Game;
using GameAudience = Tradeground.Game.Audience;
using GamePhase = Tradeground.Game.RoomPhase;

namespace Tradeground.Api.Endpoints;

/// <summary>
/// Turns game objects into the wire shapes in Tradeground.Contracts. This is the only place the two
/// meet: the domain can change freely, and only what is mapped here ever reaches a browser.
/// </summary>
public static class ContractMapping
{
    public static ModeSummary ToSummary(this GameMode mode) => new(
        mode.Id,
        mode.Name,
        mode.Audience.ToContract(),
        mode.Description,
        mode.Asset.DisplayName,
        mode.Asset.Symbol,
        mode.Asset.Unit,
        mode.StartingCash,
        mode.MaxPosition,
        mode.MaxOrderQuantity,
        mode.RoundSeconds,
        mode.HiddenValue.DiceCount,
        mode.HiddenValue.DiceSides,
        mode.HiddenValue.ValuePerPip);

    public static Contracts.Audience ToContract(this GameAudience audience) => audience switch
    {
        GameAudience.Explorer => Contracts.Audience.Explorer,
        GameAudience.Trader => Contracts.Audience.Trader,
        _ => throw new ArgumentOutOfRangeException(nameof(audience)),
    };

    public static Contracts.RoomPhase ToContract(this GamePhase phase) => phase switch
    {
        GamePhase.Trading => Contracts.RoomPhase.Trading,
        GamePhase.Settled => Contracts.RoomPhase.Settled,
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    /// <summary>Dice are 0-based inside the game and 1-based for players.</summary>
    public static ClueView ToView(this Clue clue) => new(clue.DieIndex + 1, clue.Value, clue.Text);

    public static PublicTrade ToPublic(this Trade trade) => new(trade.Sequence, trade.Price, trade.Quantity, trade.AggressorSide);

    public static OpenOrderView ToView(this Order order) => new(order.Id, order.Side, order.Price, order.Remaining);

    public static SettlementView ToView(this Settlement settlement) => new(
        settlement.TrueValue,
        settlement.Dice,
        settlement.Results.Select(r => new PlayerResultView(r.Rank, r.DisplayName, r.Cash, r.Position, r.ProfitAndLoss)).ToList());

    public static PlaceOrderResponse ToResponse(this OrderResult result) => new(
        result.OrderId,
        result.Status,
        result.FilledQuantity,
        result.RestingQuantity,
        result.Trades.Select(t => new Fill(t.Price, t.Quantity)).ToList(),
        result.RejectReason);
}
