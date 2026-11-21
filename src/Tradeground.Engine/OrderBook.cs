namespace Tradeground.Engine;

/// <summary>
/// A price-time priority limit order book with a matching engine for one symbol.
/// </summary>
/// <remarks>
/// <para>
/// NOT thread-safe, on purpose. Each book is owned by exactly one thread (see RoomActor in the API),
/// which removes the need for locks and makes every run deterministic: the same orders in the same
/// sequence always produce the same trades.
/// </para>
/// <para>
/// Data structures:
/// bids and asks are SortedDictionary&lt;price, PriceLevel&gt; (red-black tree: O(log P) insert/remove,
/// best price first when enumerating). Each level is a FIFO LinkedList, and a dictionary maps
/// order id to its list node so cancels are O(1) once the level is found.
/// </para>
/// </remarks>
public sealed class OrderBook
{
    private readonly SortedDictionary<long, PriceLevel> _bids = new(Comparer<long>.Create((a, b) => b.CompareTo(a)));
    private readonly SortedDictionary<long, PriceLevel> _asks = new();
    private readonly Dictionary<long, LinkedListNode<Order>> _ordersById = [];

    private long _nextOrderId = 1;
    private long _nextSequence = 1;

    public OrderBook(string symbol, long maxOrderQuantity = 1_000_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOrderQuantity);
        Symbol = symbol;
        MaxOrderQuantity = maxOrderQuantity;
    }

    public string Symbol { get; }
    public long MaxOrderQuantity { get; }
    public long? BestBid => _bids.Count > 0 ? _bids.Keys.First() : null;
    public long? BestAsk => _asks.Count > 0 ? _asks.Keys.First() : null;
    public int OpenOrderCount => _ordersById.Count;
    public long LastSequence => _nextSequence - 1;

    public OrderResult Submit(OrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var orderId = _nextOrderId++;
        var rejection = Validate(request);
        if (rejection is not null)
        {
            return OrderResult.Rejected(orderId, rejection);
        }

        var trades = new List<Trade>();
        var selfTradeCancels = new List<long>();
        var remaining = request.Quantity;
        var opposite = request.Side == Side.Buy ? _asks : _bids;

        while (remaining > 0 && opposite.Count > 0)
        {
            var level = opposite.Values.First();
            if (request.Type == OrderType.Limit && !Crosses(request.Side, request.LimitPrice!.Value, level.Price))
            {
                break;
            }

            remaining = MatchAgainstLevel(request, orderId, remaining, level, trades, selfTradeCancels);

            if (level.Orders.Count == 0)
            {
                opposite.Remove(level.Price);
            }
        }

        var filled = request.Quantity - remaining;

        if (remaining == 0)
        {
            return new OrderResult(orderId, OrderStatus.Filled, trades, filled, 0, selfTradeCancels, null);
        }

        if (request.Type == OrderType.Market)
        {
            // Market orders never rest: a market order with no price limit sitting on the book
            // would be filled at any price later, which is almost never what the trader wants.
            return new OrderResult(orderId, OrderStatus.RemainderCancelled, trades, filled, 0, selfTradeCancels, null);
        }

        Rest(new Order(orderId, request.TraderId, request.Side, request.LimitPrice!.Value, remaining, _nextSequence++));
        var status = filled > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Resting;
        return new OrderResult(orderId, status, trades, filled, remaining, selfTradeCancels, null);
    }

    /// <summary>Cancels a resting order. Only the trader who placed it may cancel it.</summary>
    public bool Cancel(long orderId, string traderId)
    {
        if (!_ordersById.TryGetValue(orderId, out var node) || node.Value.TraderId != traderId)
        {
            return false;
        }

        RemoveResting(node);
        return true;
    }

    public Order? FindOrder(long orderId) =>
        _ordersById.TryGetValue(orderId, out var node) ? node.Value : null;

    public BookSnapshot GetSnapshot(int depth = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        return new BookSnapshot(Symbol, LastSequence, Aggregate(_bids, depth), Aggregate(_asks, depth));
    }

    private long MatchAgainstLevel(
        OrderRequest request,
        long orderId,
        long remaining,
        PriceLevel level,
        List<Trade> trades,
        List<long> selfTradeCancels)
    {
        while (remaining > 0 && level.Orders.First is { } node)
        {
            var maker = node.Value;

            // Self-trade prevention ("cancel resting"): a trader can never trade with themselves.
            // Real exchanges do this to stop fake volume; we do it so nobody can farm ratings.
            if (maker.TraderId == request.TraderId)
            {
                selfTradeCancels.Add(maker.Id);
                RemoveResting(node);
                continue;
            }

            var quantity = Math.Min(remaining, maker.Remaining);
            remaining -= quantity;
            maker.Remaining -= quantity;
            level.TotalQuantity -= quantity;

            var isBuy = request.Side == Side.Buy;
            trades.Add(new Trade(
                Sequence: _nextSequence++,
                Price: level.Price,
                Quantity: quantity,
                BuyOrderId: isBuy ? orderId : maker.Id,
                SellOrderId: isBuy ? maker.Id : orderId,
                BuyerId: isBuy ? request.TraderId : maker.TraderId,
                SellerId: isBuy ? maker.TraderId : request.TraderId,
                AggressorSide: request.Side));

            if (maker.Remaining == 0)
            {
                level.Orders.RemoveFirst();
                _ordersById.Remove(maker.Id);
            }
        }

        return remaining;
    }

    private void Rest(Order order)
    {
        var side = order.Side == Side.Buy ? _bids : _asks;
        if (!side.TryGetValue(order.Price, out var level))
        {
            level = new PriceLevel(order.Price);
            side.Add(order.Price, level);
        }

        _ordersById[order.Id] = level.Orders.AddLast(order);
        level.TotalQuantity += order.Remaining;
    }

    private void RemoveResting(LinkedListNode<Order> node)
    {
        var order = node.Value;
        var side = order.Side == Side.Buy ? _bids : _asks;
        var level = side[order.Price];

        level.Orders.Remove(node);
        level.TotalQuantity -= order.Remaining;
        _ordersById.Remove(order.Id);

        if (level.Orders.Count == 0)
        {
            side.Remove(order.Price);
        }
    }

    private string? Validate(OrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TraderId))
        {
            return "Trader id is required.";
        }

        if (request.Quantity <= 0)
        {
            return "Quantity must be positive.";
        }

        if (request.Quantity > MaxOrderQuantity)
        {
            return $"Quantity must be at most {MaxOrderQuantity}.";
        }

        return request.Type switch
        {
            OrderType.Limit when request.LimitPrice is null or <= 0 => "Limit orders need a positive limit price.",
            OrderType.Market when request.LimitPrice is not null => "Market orders must not have a limit price.",
            _ => null,
        };
    }

    private static bool Crosses(Side side, long limitPrice, long restingPrice) =>
        side == Side.Buy ? limitPrice >= restingPrice : limitPrice <= restingPrice;

    private static List<DepthLevel> Aggregate(SortedDictionary<long, PriceLevel> side, int depth) =>
        side.Values
            .Take(depth)
            .Select(level => new DepthLevel(level.Price, level.TotalQuantity, level.Orders.Count))
            .ToList();

    private sealed class PriceLevel(long price)
    {
        public long Price { get; } = price;
        public LinkedList<Order> Orders { get; } = new();
        public long TotalQuantity { get; set; }
    }
}
