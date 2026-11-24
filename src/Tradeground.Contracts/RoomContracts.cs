using Tradeground.Engine;

namespace Tradeground.Contracts;

// Market Maker: the shapes of the room API. Domain types never cross the wire directly; the API maps
// them to these, so nothing secret (trader keys, hidden dice) can leak by accident.

public enum Audience
{
    /// <summary>Younger players: friendly goods, plain language, simple controls.</summary>
    Explorer,

    /// <summary>Teens and adults: real terminology and the full order book.</summary>
    Trader,
}

public enum RoomPhase
{
    Trading,
    Settled,
}

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
    long ValuePerPip);

public sealed record CreateRoomRequest(string ModeId, ulong? Seed = null);

public sealed record CreateRoomResponse(string Code, string HostKey, string ModeId);

/// <summary>A revealed die. <see cref="Die"/> is 1-based, as players see it.</summary>
public sealed record ClueView(int Die, int Value, string Text);

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
public sealed record PublicTrade(long Sequence, long Price, long Quantity, Side AggressorSide);

public sealed record OpenOrderView(long OrderId, Side Side, long Price, long Remaining);

/// <summary>
/// A player's private view. Includes the name and clue so a player who refreshes the page can carry
/// on with nothing but their trader key.
/// </summary>
public sealed record AccountView(
    string DisplayName,
    ClueView PrivateClue,
    long Cash,
    long Position,
    IReadOnlyList<OpenOrderView> OpenOrders);

public sealed record PlayerResultView(int Rank, string DisplayName, long Cash, long Position, long ProfitAndLoss);

/// <summary>The end of a round: the true value, every die (no longer secret) and the ranking.</summary>
public sealed record SettlementView(long TrueValue, IReadOnlyList<int> Dice, IReadOnlyList<PlayerResultView> Results);

/// <summary>The public state of a room: enough for anyone arriving late to catch up.</summary>
public sealed record RoomView(
    string Code,
    ModeSummary Mode,
    RoomPhase Phase,
    int PlayerCount,
    IReadOnlyList<ClueView> PublicClues,
    IReadOnlyList<PublicTrade> RecentTrades,
    SettlementView? Settlement);
