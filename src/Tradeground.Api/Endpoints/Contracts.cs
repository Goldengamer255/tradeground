using Tradeground.Engine;
using Tradeground.Game;

namespace Tradeground.Api.Endpoints;

// Wire contracts (DTOs). Kept separate from domain types so the API shape can stay stable
// while the engine changes, and so nothing secret (trader keys, hidden dice) ever leaks out.

public sealed record ModeSummary(string Id, string Name, Audience Audience, string Description, string Asset, int RoundSeconds)
{
    public static ModeSummary From(GameMode mode) =>
        new(mode.Id, mode.Name, mode.Audience, mode.Description, mode.Asset.DisplayName, mode.RoundSeconds);
}

public sealed record CreateRoomRequest(string ModeId, ulong? Seed);

public sealed record CreateRoomResponse(string Code, string HostKey, string ModeId);

public sealed record JoinRoomResponse(
    string TraderKey,
    string DisplayName,
    string PrivateClue,
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

public sealed record AccountView(long Cash, long Position);
