using FsCheck.Xunit;

namespace Tradeground.Engine.Tests;

/// <summary>
/// Property-based tests: instead of hand-picking scenarios, FsCheck generates hundreds of random
/// sequences of orders and cancels, and we check rules that must hold for ALL of them.
/// When a rule breaks, FsCheck "shrinks" the input to the smallest sequence that still fails.
/// </summary>
public sealed class OrderBookPropertyTests
{
    private const int Runs = 300;
    private const int MaxQuantity = 50;
    private static readonly string[] Traders = ["t0", "t1", "t2", "t3"];

    [Property(MaxTest = Runs)]
    public void Book_is_never_crossed(int[] ops)
    {
        Simulate(ops, afterEachStep: book =>
        {
            if (book.BestBid is { } bid && book.BestAsk is { } ask)
            {
                bid.ShouldBeLessThan(ask, "a crossed book means a trade that should have happened did not");
            }
        });
    }

    [Property(MaxTest = Runs)]
    public void Every_order_is_fully_accounted_for(int[] ops)
    {
        foreach (var (request, result) in Simulate(ops).Submissions)
        {
            if (result.Status == OrderStatus.Rejected)
            {
                continue;
            }

            result.FilledQuantity.ShouldBe(result.Trades.Sum(t => t.Quantity));

            if (request.Type == OrderType.Limit)
            {
                (result.FilledQuantity + result.RestingQuantity).ShouldBe(request.Quantity);
            }
            else
            {
                result.RestingQuantity.ShouldBe(0, "market orders never rest");
                result.FilledQuantity.ShouldBeLessThanOrEqualTo(request.Quantity);
            }
        }
    }

    [Property(MaxTest = Runs)]
    public void Trades_respect_both_orders_prices(int[] ops)
    {
        var simulation = Simulate(ops);
        var requestsById = simulation.Submissions.ToDictionary(s => s.Result.OrderId, s => s.Request);

        foreach (var (request, result) in simulation.Submissions)
        {
            foreach (var trade in result.Trades)
            {
                trade.Price.ShouldBe(requestsById[trade.MakerOrderId].LimitPrice!.Value, "trades happen at the maker's price");
                trade.BuyerId.ShouldNotBe(trade.SellerId, "self-trades are prevented");

                if (request.Type == OrderType.Limit)
                {
                    var limit = request.LimitPrice!.Value;
                    if (request.Side == Side.Buy)
                    {
                        trade.Price.ShouldBeLessThanOrEqualTo(limit);
                    }
                    else
                    {
                        trade.Price.ShouldBeGreaterThanOrEqualTo(limit);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Model-based test: track what SHOULD be resting with a trivially simple model
    /// (a dictionary), then check the real, optimised book agrees with it exactly.
    /// </summary>
    [Property(MaxTest = Runs)]
    public void Book_matches_a_simple_model_of_resting_quantity(int[] ops)
    {
        var simulation = Simulate(ops);
        var model = new Dictionary<long, long>();

        foreach (var step in simulation.Steps)
        {
            switch (step)
            {
                case SubmitStep { Result: var result }:
                    foreach (var trade in result.Trades)
                    {
                        model[trade.MakerOrderId] -= trade.Quantity;
                        model[trade.MakerOrderId].ShouldBeGreaterThanOrEqualTo(0);
                        if (model[trade.MakerOrderId] == 0)
                        {
                            model.Remove(trade.MakerOrderId);
                        }
                    }

                    foreach (var cancelled in result.SelfTradeCancelledOrderIds)
                    {
                        model.Remove(cancelled);
                    }

                    if (result.RestingQuantity > 0)
                    {
                        model[result.OrderId] = result.RestingQuantity;
                    }

                    break;

                case CancelStep { OrderId: var id, Cancelled: true }:
                    model.Remove(id);
                    break;
            }
        }

        var book = simulation.Book;
        book.OpenOrderCount.ShouldBe(model.Count);
        foreach (var (orderId, remaining) in model)
        {
            book.FindOrder(orderId).ShouldNotBeNull().Remaining.ShouldBe(remaining);
        }

        var snapshot = book.GetSnapshot(depth: int.MaxValue);
        (snapshot.Bids.Sum(l => l.Quantity) + snapshot.Asks.Sum(l => l.Quantity)).ShouldBe(model.Values.Sum());
    }

    [Property(MaxTest = 100)]
    public void Same_orders_always_produce_the_same_trades(int[] ops)
    {
        var first = Simulate(ops).Submissions.SelectMany(s => s.Result.Trades).ToList();
        var second = Simulate(ops).Submissions.SelectMany(s => s.Result.Trades).ToList();

        second.ShouldBe(first);
    }

    private abstract record Step;

    private sealed record SubmitStep(OrderRequest Request, OrderResult Result) : Step;

    private sealed record CancelStep(long OrderId, bool Cancelled) : Step;

    private sealed record Simulation(OrderBook Book, IReadOnlyList<Step> Steps)
    {
        public IEnumerable<(OrderRequest Request, OrderResult Result)> Submissions =>
            Steps.OfType<SubmitStep>().Select(s => (s.Request, s.Result));
    }

    /// <summary>
    /// Turns FsCheck's random integers into a realistic order flow: 4 traders, prices clustered
    /// around 100 so orders often cross, ~1 in 8 market orders, ~1 in 16 cancels.
    /// </summary>
    private static Simulation Simulate(int[] ops, Action<OrderBook>? afterEachStep = null)
    {
        var book = new OrderBook("PROP", MaxQuantity);
        var steps = new List<Step>();
        var submitted = new List<SubmitStep>();

        foreach (var raw in ops ?? [])
        {
            var x = Mix((uint)raw);
            var trader = Traders[x & 3];

            if (((x >> 2) & 15) == 0 && submitted.Count > 0)
            {
                var target = submitted[(int)((x >> 6) % (uint)submitted.Count)];
                steps.Add(new CancelStep(target.Result.OrderId, book.Cancel(target.Result.OrderId, target.Request.TraderId)));
            }
            else
            {
                var side = ((x >> 6) & 1) == 0 ? Side.Buy : Side.Sell;
                var quantity = 1 + (long)((x >> 16) % MaxQuantity);
                var request = ((x >> 7) & 7) == 0
                    ? OrderRequest.Market(trader, side, quantity)
                    : OrderRequest.Limit(trader, side, 90 + (long)((x >> 10) % 21), quantity);

                var step = new SubmitStep(request, book.Submit(request));
                steps.Add(step);
                submitted.Add(step);
            }

            afterEachStep?.Invoke(book);
        }

        return new Simulation(book, steps);
    }

    /// <summary>Integer hash so small generated numbers still spread across all fields.</summary>
    private static uint Mix(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352DU;
        x ^= x >> 15;
        x *= 0x846CA68BU;
        x ^= x >> 16;
        return x;
    }
}
