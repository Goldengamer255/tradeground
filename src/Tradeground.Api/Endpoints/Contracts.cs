using Tradeground.Engine;
using Tradeground.Game;

namespace Tradeground.Api.Endpoints;

// Wire contracts (DTOs). Kept separate from domain types so the API shape can stay stable
// while the engine changes, and so nothing secret (trader keys, hidden dice) ever leaks out.

/// <summary>
/// Everything a client needs to draw a mode. The dice SHAPE (count, sides, value per pip) is public:
/// players need it to estimate fair value. Only the dice VALUES are secret.
/// </summary>
public sealed record ModeSummary(
    string Id,
    string Name,
    Audience Audience,
    string Description,
    string Asset,
    string Symbol,
    string Unit,
    long StartingCash,
    long MaxPosition,
    long MaxOrderQuantity,
    int RoundSeconds,
    int DiceCount,
    int DiceSides,
    long ValuePerPip)
{
    public static ModeSummary From(GameMode mode) =>
        new(
            mode.Id,
            mode.Name,
            mode.Audience,
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
}

public sealed record CreateRoomRequest(string ModeId, ulong? Seed);

public sealed record CreateRoomResponse(string Code, string HostKey, string ModeId);

/// <summary>A revealed die. <see cref="Die"/> is 1-based, as players see it.</summary>
public sealed record ClueView(int Die, int Value, string Text)
{
    public static ClueView From(Clue clue) => new(clue.DieIndex + 1, clue.Value, clue.Text);
}

public sealed record JoinRoomResponse(
    string TraderKey,
    string DisplayName,
    ClueView PrivateClue,
    long StartingCash,
    long MaxPosition);

public sealed record PlaceOrderRequest(Side Side, OrderType Type, long Quantity, long? LimitPrice);

public sealed record Fill(long Price, long Quantity);

public sealed record PlaceOrderResponse(
    long OrderId,
    OrderStatus Status,
    long FilledQuantity,
    long RestingQuantity,
    IReadOnlyList<Fill> Fills,
    string? RejectReason);

/// <summary>What everyone sees on the trade tape: no trader identities.</summary>
public sealed record PublicTrade(long Sequence, long Price, long Quantity, Side AggressorSide)
{
    public static PublicTrade From(Trade trade) => new(trade.Sequence, trade.Price, trade.Quantity, trade.AggressorSide);
}

public sealed record OpenOrderView(long OrderId, Side Side, long Price, long Remaining)
{
    public static OpenOrderView From(Order order) => new(order.Id, order.Side, order.Price, order.Remaining);
}

/// <summary>
/// A player's private view. Includes the name and clue so a player who refreshes the page can
/// carry on with nothing but their trader key.
/// </summary>
public sealed record AccountView(
    string DisplayName,
    ClueView PrivateClue,
    long Cash,
    long Position,
    IReadOnlyList<OpenOrderView> OpenOrders);

/// <summary>The public state of a room: enough for anyone arriving late to catch up.</summary>
public sealed record RoomView(
    string Code,
    ModeSummary Mode,
    RoomPhase Phase,
    int PlayerCount,
    IReadOnlyList<ClueView> PublicClues,
    IReadOnlyList<PublicTrade> RecentTrades,
    Settlement? Settlement);
