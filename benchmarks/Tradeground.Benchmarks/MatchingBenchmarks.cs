using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Tradeground.Engine;

BenchmarkSwitcher.FromAssembly(typeof(MatchingBenchmarks).Assembly).Run(args);

/// <summary>
/// Measures raw engine throughput: how long N realistic orders take to match.
/// orders/sec = OrderCount / Mean. MemoryDiagnoser shows allocations per run (GC pressure).
/// </summary>
[MemoryDiagnoser]
public class MatchingBenchmarks
{
    private OrderRequest[] _orders = [];

    [Params(10_000, 100_000)]
    public int OrderCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(7);
        _orders = Enumerable.Range(0, OrderCount)
            .Select(i =>
            {
                var trader = $"t{i % 16}";
                var side = random.Next(2) == 0 ? Side.Buy : Side.Sell;
                var quantity = random.Next(1, 20);
                return random.Next(10) == 0
                    ? OrderRequest.Market(trader, side, quantity)
                    : OrderRequest.Limit(trader, side, random.Next(95, 106), quantity);
            })
            .ToArray();
    }

    [Benchmark]
    public long SubmitAllOrders()
    {
        var book = new OrderBook("BENCH");
        long trades = 0;
        foreach (var order in _orders)
        {
            trades += book.Submit(order).Trades.Count;
        }

        return trades;
    }
}
