using Tradeground.Engine;

namespace Tradeground.Game;

public enum RoomPhase
{
    Trading,
    Settled,
}

public sealed record Player(string TraderId, string DisplayName, Clue PrivateClue);

public sealed record PlayerResult(int Rank, string DisplayName, long Cash, long Position, long ProfitAndLoss);

public sealed record Settlement(long TrueValue, IReadOnlyList<PlayerResult> Results);

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

    public Player Join(string traderId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traderId);
        if (Phase != RoomPhase.Trading)
        {
            throw new InvalidOperationException("The round has already been settled.");
        }

        var player = new Player(traderId, displayName, _round.NextPrivateClue());
        _ledger.Open(traderId);
        _players.Add(traderId, player);
        return player;
    }

    public bool IsPlayer(string traderId) => _players.ContainsKey(traderId);

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

        _settlement = new Settlement(trueValue, ranked);
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
