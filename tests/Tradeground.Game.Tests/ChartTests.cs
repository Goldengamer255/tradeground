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
    public void Ships_many_moments_across_four_markets()
    {
        Library.Moments.Count.ShouldBeGreaterThanOrEqualTo(100);
        Library.Moments.Select(m => m.Symbol).Distinct().ShouldBe(new[] { "BTC/USDT", "ETH/USDT", "BNB/USDT", "XRP/USDT" });
        Library.Source.ShouldContain("CC BY-NC-SA 4.0");
    }

    /// <summary>The heart of "one chart, many timeframes": every zoom level ends at the same instant and price.</summary>
    [Fact]
    public void Every_timeframe_of_a_moment_ends_at_the_same_now_and_price()
    {
        foreach (var moment in Library.Moments)
        {
            foreach (var timeframe in ChartLibrary.Timeframes)
            {
                var frame = moment.Frame(timeframe);
                frame.Count.ShouldBe(ChartMoment.CandlesPerFrame);
                frame[ChartMoment.HistoryCandles].OpenTime.ShouldBe(moment.Now);
                frame[ChartMoment.HistoryCandles - 1].Close.ShouldBe(moment.PriceNow);
            }
        }
    }

    [Fact]
    public void A_moment_with_a_gap_is_rejected()
    {
        var good = Library.Moments[0];
        var broken = good.Frame(Timeframe.H1).ToList();
        broken[10] = broken[10] with { OpenTime = broken[10].OpenTime.AddMinutes(5) };
        var frames = good.Frames.ToDictionary(f => f.Key, f => f.Value);
        frames[Timeframe.H1.Id] = broken;

        Should.Throw<InvalidOperationException>(() => ChartLibrary.Validate(good with { Frames = frames }))
            .Message.ShouldContain("1h");
    }
}

public sealed class ChartRoundTests
{
    private static readonly ChartLibrary Library = ChartLibrary.LoadBuiltIn();
    private static readonly DateTimeOffset Now = new(2024, 1, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A hand-made moment: flat at 100 until "now", then a straight line to <paramref name="end"/> on every timeframe.</summary>
    private static ChartRound RoundEndingAt(long end)
    {
        var frames = ChartLibrary.Timeframes.ToDictionary(
            t => t.Id,
            t => (IReadOnlyList<Candle>)Enumerable.Range(0, ChartMoment.CandlesPerFrame)
                .Select(i => i < ChartMoment.HistoryCandles ? 100L : 100 + ((end - 100) * (i - ChartMoment.HistoryCandles + 1) / ChartMoment.FutureCandles))
                .Select((price, i) => new Candle(Now + (t.Length * (i - ChartMoment.HistoryCandles)), price, price, price, price))
                .ToList());
        var moment = new ChartMoment("TEST", "Test", 0, Now, frames);
        ChartLibrary.Validate(moment);
        return new ChartRound(moment);
    }

    [Theory]
    [InlineData(120, Direction.Up, true)]
    [InlineData(120, Direction.Down, false)]
    [InlineData(80, Direction.Down, true)]
    [InlineData(80, Direction.Up, false)]
    public void A_call_is_right_when_the_price_ends_on_that_side(long end, Direction call, bool right)
    {
        RoundEndingAt(end).Call(call, Timeframe.M15).Correct.ShouldBe(right);
    }

    [Fact]
    public void A_flat_ending_is_neither_right_nor_wrong()
    {
        var result = RoundEndingAt(100).Call(Direction.Up, Timeframe.H1);

        result.Correct.ShouldBeNull();
        result.Actual.ShouldBeNull();
    }

    [Fact]
    public void The_call_looks_ahead_20_candles_of_the_timeframe_it_was_made_on()
    {
        RoundEndingAt(120).Call(Direction.Up, Timeframe.M1).Horizon.ShouldBe(TimeSpan.FromMinutes(20));
        RoundEndingAt(120).Call(Direction.Up, Timeframe.H4).Horizon.ShouldBe(TimeSpan.FromHours(80));
    }

    [Fact]
    public void The_future_is_hidden_on_every_timeframe_until_the_call()
    {
        var round = RoundEndingAt(120);

        ChartLibrary.Timeframes.ShouldAllBe(t => round.Visible(t).Count == ChartMoment.HistoryCandles);
        round.Call(Direction.Up, Timeframe.M30);
        ChartLibrary.Timeframes.ShouldAllBe(t => round.Visible(t).Count == ChartMoment.CandlesPerFrame, "after the call, every zoom level shows what happened");
    }

    [Fact]
    public void A_chart_can_only_be_called_once()
    {
        var round = RoundEndingAt(120);
        round.Call(Direction.Up, Timeframe.M1);

        Should.Throw<InvalidOperationException>(() => round.Call(Direction.Down, Timeframe.H4));
    }

    [Fact]
    public void Now_is_shown_as_100_on_every_timeframe()
    {
        var round = ChartRound.Pick(Library, new DeterministicRandom(3));

        foreach (var timeframe in ChartLibrary.Timeframes)
        {
            round.Rebase(round.Visible(timeframe)[^1]).Close.ShouldBe(ChartRound.IndexBase, timeframe.Id);
        }
    }
}

public sealed class ChartRunTests
{
    private static readonly ChartLibrary Library = ChartLibrary.LoadBuiltIn();

    [Fact]
    public void Calling_before_dealing_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => new ChartRun(Library, 1).Call(Direction.Up, Timeframe.M1));
    }

    /// <summary>For any run of charts called on any timeframes, the tally always adds up and streaks make sense.</summary>
    [Property(MaxTest = 100)]
    public void The_tally_always_adds_up(ulong seed, byte[] plays)
    {
        var run = new ChartRun(Library, seed);
        var calls = 0;

        foreach (var play in (plays ?? []).Take(40))
        {
            run.Next();
            var timeframe = ChartLibrary.Timeframes[play % ChartLibrary.Timeframes.Count];
            var result = run.Call((play & 0x80) == 0 ? Direction.Up : Direction.Down, timeframe);
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
