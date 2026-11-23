using Tradeground.Engine;

namespace Tradeground.Game;

public enum RoomPhase
{
    Trading,
    Settled,
}

public sealed record Player(string TraderId, string DisplayName, Clue PrivateClue);

public sealed record PlayerResult(int Rank, string DisplayName, long Cash, long Position, long ProfitAndLoss);

/// <summary>The end of a round. The dice are no longer secret, so they are revealed here.</summary>
public sealed record Settlement(long TrueValue, IReadOnlyList<int> Dice, IReadOnlyList<PlayerResult> Results);

/// <summary>
/// One round of a game: an order book, a ledger, the hidden value and per-player risk limits.
/// </summary>
/// <remarks>Not thread-safe; the API gives each room a single-threaded owner.</remarks>
public sealed class GameRoom
{
    private readonly OrderBook _book;
    private readonly Ledger _ledger;
    private readonly HiddenValueRound _round;
    private readonly Dictionary<string, Player> _players = [];
    private readonly Dictionary<long, OpenOrder> _openOrders = [];
    private readonly List<Trade> _tape = [];
    private Settlement? _settlement;

    public GameRoom(GameMode mode, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(mode);
        Mode = mode;
        _book = new OrderBook(mode.Asset.Symbol, mode.MaxOrderQuantity);
        _ledger = new Ledger(mode.StartingCash);
        _round = new HiddenValueRound(mode.HiddenValue, seed);
    }

    public GameMode Mode { get; }
    public RoomPhase Phase { get; private set; } = RoomPhase.Trading;
    public IReadOnlyCollection<Player> Players => _players.Values;
    public IReadOnlyList<Clue> PublicClues => _round.PublicClues;
    public Ledger Ledger => _ledger;

    /// <summary>Every trade in this round, oldest first. Lets late joiners catch up on the tape.</summary>
    public IReadOnlyList<Trade> Tape => _tape;

    /// <summary>The final result, once the round has been settled.</summary>
    public Settlement? Settlement => _settlement;

    public Player Join(string traderId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (Phase != RoomPhase.Trading)
        {
            throw new InvalidOperationException("The round has already been settled.");
        }

        // Names are how players find themselves on the results table, so they must be unique.
        if (IsNameTaken(displayName))
        {
            throw new InvalidOperationException($"The name {displayName} is already taken in this room.");
        }

        var player = new Player(traderId, displayName, _round.NextPrivateClue());
        _ledger.Open(traderId);
        _players.Add(traderId, player);
        return player;
    }

    public bool IsPlayer(string traderId) => _players.ContainsKey(traderId);

    public bool IsNameTaken(string displayName) =>
        _players.Values.Any(p => string.Equals(p.DisplayName, displayName, StringComparison.Ordinal));

    public Player GetPlayer(string traderId) => _players[traderId];

    /// <summary>
    /// A player's resting orders, oldest first. Read from the book (the source of truth for price
    /// and remaining size) and cross-checked against the room's own risk tracking: if the two ever
    /// disagree, the risk checks would be wrong, so fail loudly instead of returning bad data.
    /// </summary>
    public IReadOnlyList<Order> OpenOrdersFor(string traderId) =>
        _openOrders
            .Where(kv => kv.Value.TraderId == traderId)
            .Select(kv => _book.FindOrder(kv.Key) is { } order && order.Remaining == kv.Value.Remaining
                ? order
                : throw new InvalidOperationException($"Risk tracking is out of sync with the book for order {kv.Key}."))
            .OrderBy(o => o.Sequence)
            .ToList();

    public OrderResult Submit(OrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Phase != RoomPhase.Trading)
        {
            return OrderResult.Rejected(0, "The round is over.");
        }

        if (!_players.ContainsKey(request.TraderId))
        {
            return OrderResult.Rejected(0, "Unknown player.");
        }

        var riskRejection = CheckRisk(request);
        if (riskRejection is not null)
        {
            return OrderResult.Rejected(0, riskRejection);
        }

        var result = _book.Submit(request);
        _tape.AddRange(result.Trades);

        foreach (var trade in result.Trades)
        {
            _ledger.Apply(trade);
            ReduceOpenOrder(trade.MakerOrderId, trade.Quantity);
        }

        foreach (var cancelledId in result.SelfTradeCancelledOrderIds)
        {
            _openOrders.Remove(cancelledId);
        }

        if (result.RestingQuantity > 0)
        {
            _openOrders[result.OrderId] = new OpenOrder(request.TraderId, request.Side, result.RestingQuantity);
        }

        return result;
    }

    public bool Cancel(string traderId, long orderId)
    {
        if (Phase != RoomPhase.Trading || !_book.Cancel(orderId, traderId))
        {
            return false;
        }

        _openOrders.Remove(orderId);
        return true;
    }

    public Clue? RevealPublicClue() => Phase == RoomPhase.Trading ? _round.RevealNextPublicClue() : null;

    public BookSnapshot Snapshot(int depth = 10) => _book.GetSnapshot(depth);

    /// <summary>
    /// Ends the round. Every position is closed at the true value.
    /// P&amp;L = final cash + position x true value - starting cash. Across all players it sums to zero.
    /// </summary>
    public Settlement Settle()
    {
        if (_settlement is not null)
        {
            return _settlement;
        }

        Phase = RoomPhase.Settled;
        var trueValue = _round.TrueValue;

        // The contract has expired, so nothing can trade any more: cancel every resting order,
        // exactly as an exchange does at expiry. Leaving them would show a "live" book that isn't.
        foreach (var (orderId, open) in _openOrders)
        {
            _book.Cancel(orderId, open.TraderId);
        }

        _openOrders.Clear();

        var ranked = _players.Values
            .Select(p =>
            {
                var account = _ledger.Get(p.TraderId);
                var pnl = account.Cash + (account.Position * trueValue) - _ledger.StartingCash;
                return (Player: p, Account: account, Pnl: pnl);
            })
            .OrderByDescending(x => x.Pnl)
            .ThenBy(x => x.Player.DisplayName, StringComparer.Ordinal)
            .Select((x, i) => new PlayerResult(i + 1, x.Player.DisplayName, x.Account.Cash, x.Account.Position, x.Pnl))
            .ToList();

        _settlement = new Settlement(trueValue, _round.Dice, ranked);
        return _settlement;
    }

    /// <summary>
    /// Pre-trade risk check: assume every open order on this side fills. If that worst case
    /// would break the position limit, reject before the order ever reaches the book.
    /// </summary>
    private string? CheckRisk(OrderRequest request)
    {
        var position = _ledger.Get(request.TraderId).Position;
        var openSameSide = _openOrders.Values
            .Where(o => o.TraderId == request.TraderId && o.Side == request.Side)
            .Sum(o => o.Remaining);

        var worstCase = request.Side == Side.Buy
            ? position + openSameSide + request.Quantity
            : -position + openSameSide + request.Quantity;

        return worstCase > Mode.MaxPosition
            ? $"Position limit: this order could take you past {Mode.MaxPosition} {Mode.Asset.Unit}."
            : null;
    }

    private void ReduceOpenOrder(long orderId, long filled)
    {
        if (!_openOrders.TryGetValue(orderId, out var open))
        {
            return;
        }

        var remaining = open.Remaining - filled;
        if (remaining <= 0)
        {
            _openOrders.Remove(orderId);
        }
        else
        {
            _openOrders[orderId] = open with { Remaining = remaining };
        }
    }

    private sealed record OpenOrder(string TraderId, Side Side, long Remaining);
}
