namespace Tradeground.Engine;

public enum Side
{
    Buy,
    Sell,
}

public enum OrderType
{
    /// <summary>Trade at the given price or better; any unfilled remainder rests on the book.</summary>
    Limit,

    /// <summary>Trade immediately at the best available prices; any unfilled remainder is cancelled.</summary>
    Market,
}

public enum OrderStatus
{
    /// <summary>Failed validation. Nothing happened.</summary>
    Rejected,

    /// <summary>No fills; the whole order is resting on the book.</summary>
    Resting,

    /// <summary>Some fills; the remainder is resting on the book.</summary>
    PartiallyFilled,

    /// <summary>Completely filled.</summary>
    Filled,

    /// <summary>A market order that ran out of liquidity; the unfilled remainder was discarded.</summary>
    RemainderCancelled,
}

/// <summary>
/// What a trader asks for. Prices and quantities are integers ("ticks"), never floating point,
/// so 0.1 + 0.2 rounding errors can never create or destroy money.
/// </summary>
public sealed record OrderRequest(string TraderId, Side Side, OrderType Type, long Quantity, long? LimitPrice = null)
{
    public static OrderRequest Limit(string traderId, Side side, long price, long quantity) =>
        new(traderId, side, OrderType.Limit, quantity, price);

    public static OrderRequest Market(string traderId, Side side, long quantity) =>
        new(traderId, side, OrderType.Market, quantity);
}

/// <summary>An order resting on the book, waiting to be matched.</summary>
public sealed class Order
{
    internal Order(long id, string traderId, Side side, long price, long quantity, long sequence)
    {
        Id = id;
        TraderId = traderId;
        Side = side;
        Price = price;
        Quantity = quantity;
        Remaining = quantity;
        Sequence = sequence;
    }

    public long Id { get; }
    public string TraderId { get; }
    public Side Side { get; }
    public long Price { get; }
    public long Quantity { get; }
    public long Remaining { get; internal set; }

    /// <summary>Arrival order. Lower sequence = earlier = higher time priority.</summary>
    public long Sequence { get; }
}

/// <summary>
/// One execution between an incoming (aggressor) order and a resting (maker) order.
/// Always executes at the maker's price.
/// </summary>
public readonly record struct Trade(
    long Sequence,
    long Price,
    long Quantity,
    long BuyOrderId,
    long SellOrderId,
    string BuyerId,
    string SellerId,
    Side AggressorSide)
{
    public long MakerOrderId => AggressorSide == Side.Buy ? SellOrderId : BuyOrderId;
}

public sealed record OrderResult(
    long OrderId,
    OrderStatus Status,
    IReadOnlyList<Trade> Trades,
    long FilledQuantity,
    long RestingQuantity,
    IReadOnlyList<long> SelfTradeCancelledOrderIds,
    string? RejectReason)
{
    public static OrderResult Rejected(long orderId, string reason) =>
        new(orderId, OrderStatus.Rejected, [], 0, 0, [], reason);
}

public readonly record struct DepthLevel(long Price, long Quantity, int OrderCount);

/// <summary>
/// Aggregated view of the book. <see cref="LastSequence"/> lets clients drop snapshots
/// that arrive out of order over the network.
/// </summary>
public sealed record BookSnapshot(
    string Symbol,
    long LastSequence,
    IReadOnlyList<DepthLevel> Bids,
    IReadOnlyList<DepthLevel> Asks);
