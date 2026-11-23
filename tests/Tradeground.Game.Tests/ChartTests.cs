using FsCheck.Xunit;
using Tradeground.Game.Charts;

namespace Tradeground.Game.Tests;

public sealed class WeeklyCandleTests
{
    private static readonly DateOnly Monday = new(2024, 1, 1);
    private static readonly DateTimeOffset MondayUtc = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Daily_prices_become_one_candle_per_week()
    {
        var daily = new (DateOnly, long)[]
        {
            (Monday, 100), (Monday.AddDays(1), 105), (Monday.AddDays(2), 98), (Monday.AddDays(4), 102),
            (Monday.AddDays(7), 103), (Monday.AddDays(8), 101),
        };

        var candles = WeeklyCandles.FromDaily(daily);

        candles.ShouldBe(new[]
        {
            new Candle(MondayUtc, Open: 100, High: 105, Low: 98, Close: 102),
            new Candle(MondayUtc.AddDays(7), Open: 103, High: 103, Low: 101, Close: 101),
        });
    }

    [Fact]
    public void Weeks_start_on_monday()
    {
        WeeklyCandles.StartOfWeek(new DateOnly(2024, 1, 7)).ShouldBe(MondayUtc, "Sunday belongs to the week that started Monday");
        WeeklyCandles.StartOfWeek(Monday).ShouldBe(MondayUtc);
    }

    [Fact]
    public void Out_of_order_days_are_rejected()
    {
        Should.Throw<ArgumentException>(() => WeeklyCandles.FromDaily([(Monday.AddDays(1), 100L), (Monday, 101L)]));
    }
}

public sealed class ChartLibraryTests
{
    private static readonly ChartLibrary Library = ChartLibrary.LoadBuiltIn();

    [Fact]
    public void Every_requested_timeframe_has_charts()
    {
        Library.Timeframes.Select(t => t.Id).ShouldBe(new[] { "1m", "15m", "30m", "1h", "4h", "1w" });
    }

    [Fact]
    public void Intraday_charts_are_crypto_and_weekly_charts_are_forex()
    {
        Library.SeriesFor(Timeframe.H1).Select(s => s.Symbol).ShouldBe(new[] { "BTC/USDT", "ETH/USDT", "BNB/USDT", "XRP/USDT" });
        Library.SeriesFor(Timeframe.W1).Select(s => s.Symbol)
            .ShouldBe(new[] { "EUR/USD", "GBP/USD", "USD/JPY", "AUD/USD", "USD/CAD", "USD/CHF" });
    }

    [Fact]
    public void Candles_are_spaced_by_their_timeframe()
    {
        foreach (var timeframe in Library.Timeframes.Where(t => t != Timeframe.W1))
        {
            foreach (var segment in Library.SeriesFor(timeframe).SelectMany(s => s.Segments))
            {
                // Exchanges occasionally pause, so allow a rare gap, but never candles closer than the timeframe.
                var gaps = segment.Zip(segment.Skip(1), (a, b) => b.OpenTime - a.OpenTime).ToList();
                gaps.ShouldAllBe(g => g >= timeframe.Length);
                gaps.Count(g => g == timeframe.Length).ShouldBeGreaterThan(gaps.Count * 9 / 10, $"{timeframe.Id} candles should be evenly spaced");
            }
        }
    }

    [Fact]
    public void Sources_are_credited()
    {
        Library.Sources.ShouldContain(s => s.Contains("Federal Reserve", StringComparison.Ordinal));
        Library.Sources.ShouldContain(s => s.Contains("CC BY-NC-SA 4.0", StringComparison.Ordinal));
    }
}

public sealed class ChartRoundTests
{
    private static readonly ChartLibrary Library = ChartLibrary.LoadBuiltIn();
    private static readonly DateTimeOffset T0 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A hand-made chart: flat at 100 for the history, then a straight line to <paramref name="end"/>.</summary>
    private static ChartRound RoundEndingAt(long end)
    {
        var candles = Enumerable.Range(0, ChartRound.TotalCandles)
            .Select(i => i < ChartRound.HistoryCandles ? 100L : 100 + ((end - 100) * (i - ChartRound.HistoryCandles + 1) / ChartRound.Horizon))
            .Select((price, i) => new Candle(T0.AddHours(i), price, price, price, price))
            .ToList();
        var series = new ChartSeries("TEST", "Test", Timeframe.H1.Id, 0, [candles]);
        return new ChartRound(series, Timeframe.H1, candles, start: 0);
    }

    [Theory]
    [InlineData(120, Direction.Up, true)]
    [InlineData(120, Direction.Down, false)]
    [InlineData(80, Direction.Down, true)]
    [InlineData(80, Direction.Up, false)]
    public void A_call_is_right_when_the_price_ends_on_that_side(long end, Direction call, bool right)
    {
        RoundEndingAt(end).Call(call).Correct.ShouldBe(right);
    }

    [Fact]
    public void A_flat_ending_is_neither_right_nor_wrong()
    {
        var result = RoundEndingAt(100).Call(Direction.Up);

        result.Correct.ShouldBeNull();
        result.Actual.ShouldBeNull();
    }

    [Fact]
    public void The_future_is_hidden_until_the_call()
    {
        var round = RoundEndingAt(120);

        round.Visible.Count.ShouldBe(ChartRound.HistoryCandles);
        round.Call(Direction.Up);
        round.Visible.Count.ShouldBe(ChartRound.TotalCandles);
    }

    [Fact]
    public void A_chart_can_only_be_called_once()
    {
        var round = RoundEndingAt(120);
        round.Call(Direction.Up);

        Should.Throw<InvalidOperationException>(() => round.Call(Direction.Down));
    }

    [Fact]
    public void Prices_are_shown_as_an_index_starting_at_100()
    {
        var round = ChartRound.Pick(Library, Timeframe.M15, new DeterministicRandom(3));

        round.Rebase(round.Visible[0]).Close.ShouldBe(ChartRound.IndexBase);
    }

    [Fact]
    public void The_same_seed_deals_the_same_chart()
    {
        var a = ChartRound.Pick(Library, Timeframe.H4, new DeterministicRandom(2026));
        var b = ChartRound.Pick(Library, Timeframe.H4, new DeterministicRandom(2026));

        (a.Series.Symbol, a.Visible[0]).ShouldBe((b.Series.Symbol, b.Visible[0]));
    }
}

public sealed class ChartRunTests
{
    private static readonly ChartLibrary Library = ChartLibrary.LoadBuiltIn();

    [Fact]
    public void Calling_before_dealing_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => new ChartRun(Library, 1).Call(Direction.Up));
    }

    /// <summary>For any run of charts on any timeframes, the tally always adds up and streaks make sense.</summary>
    [Property(MaxTest = 100)]
    public void The_tally_always_adds_up(ulong seed, byte[] plays)
    {
        var run = new ChartRun(Library, seed);
        var calls = 0;

        foreach (var play in (plays ?? []).Take(40))
        {
            run.Next(Library.Timeframes[play % Library.Timeframes.Count]);
            var result = run.Call((play & 0x80) == 0 ? Direction.Up : Direction.Down);
            calls++;

            result.Correct.ShouldBe(result.Actual is null ? null : result.Actual == result.Call);
            if (result.Correct == false)
            {
                run.Streak.ShouldBe(0, "a wrong call ends the streak");
            }
        }

        (run.Right + run.Wrong + run.Flat).ShouldBe(calls);
        run.Streak.ShouldBeLessThanOrEqualTo(run.BestStreak);
        run.BestStreak.ShouldBeLessThanOrEqualTo(run.Right);
    }
}
