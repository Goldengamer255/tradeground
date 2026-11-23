using FsCheck.Xunit;
using Tradeground.Game.Charts;

namespace Tradeground.Game.Tests;

public sealed class WeeklyCandleTests
{
    private static readonly DateOnly Monday = new(2024, 1, 1);

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
            new Candle(Monday, Open: 100, High: 105, Low: 98, Close: 102),
            new Candle(Monday.AddDays(7), Open: 103, High: 103, Low: 101, Close: 101),
        });
    }

    [Fact]
    public void Weeks_start_on_monday()
    {
        WeeklyCandles.StartOfWeek(new DateOnly(2024, 1, 7)).ShouldBe(Monday, "Sunday belongs to the week that started Monday");
        WeeklyCandles.StartOfWeek(Monday).ShouldBe(Monday);
    }

    [Fact]
    public void Out_of_order_days_are_rejected()
    {
        Should.Throw<ArgumentException>(() => WeeklyCandles.FromDaily([(Monday.AddDays(1), 100L), (Monday, 101L)]));
    }
}

public sealed class ScoringTests
{
    [Theory]
    [InlineData(Prediction.StrongBull, Move.Up, 96)]
    [InlineData(Prediction.Bull, Move.Up, 64)]
    [InlineData(Prediction.Unsure, Move.Up, 0)]
    [InlineData(Prediction.Bear, Move.Up, -96)]
    [InlineData(Prediction.StrongBear, Move.Up, -224)]
    [InlineData(Prediction.StrongBear, Move.Down, 96)]
    [InlineData(Prediction.StrongBull, Move.Down, -224)]
    [InlineData(Prediction.StrongBull, Move.Flat, 0)]
    public void Points_reward_being_right_and_punish_overconfidence(Prediction prediction, Move move, int expected)
    {
        Scoring.Points(prediction, move).ShouldBe(expected);
    }

    /// <summary>
    /// The property that makes the rule "proper": if the real chance of an up-move is q, the
    /// prediction with the best EXPECTED score is the one that states q. Honesty is the best strategy.
    /// </summary>
    [Theory]
    [InlineData(10, Prediction.StrongBear)]
    [InlineData(30, Prediction.Bear)]
    [InlineData(50, Prediction.Unsure)]
    [InlineData(70, Prediction.Bull)]
    [InlineData(90, Prediction.StrongBull)]
    public void Saying_what_you_believe_maximises_expected_points(int chanceOfUp, Prediction honest)
    {
        double Expected(Prediction p) =>
            ((chanceOfUp / 100.0) * Scoring.Points(p, Move.Up)) + ((1 - (chanceOfUp / 100.0)) * Scoring.Points(p, Move.Down));

        Enum.GetValues<Prediction>().MaxBy(Expected).ShouldBe(honest);
    }
}

public sealed class ChartSessionTests
{
    private static readonly ChartLibrary Library = ChartLibrary.LoadBuiltIn();

    [Fact]
    public void Built_in_library_has_six_currency_pairs_of_valid_candles()
    {
        Library.Series.Select(s => s.Symbol).ShouldBe(new[] { "EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "USDCAD", "USDCHF" });
        Library.Series.ShouldAllBe(s => s.Candles.Count > 1_000 && s.Candles.All(c => c.IsValid));
        Library.Source.ShouldContain("Federal Reserve");
    }

    [Fact]
    public void A_game_starts_with_history_and_hides_the_future()
    {
        var session = new ChartSession(Library, seed: 7);

        session.Visible.Count.ShouldBe(ChartSession.HistoryCandles);
        session.Visible.ShouldBe(session.All.Take(ChartSession.HistoryCandles));
        session.RoundsPlayed.ShouldBe(0);
    }

    [Fact]
    public void Each_guess_reveals_exactly_one_more_week()
    {
        var session = new ChartSession(Library, seed: 7);
        var next = session.All[ChartSession.HistoryCandles];

        var result = session.Guess(Prediction.Bull);

        result.Revealed.ShouldBe(next);
        session.Visible.Count.ShouldBe(ChartSession.HistoryCandles + 1);
        session.Score.ShouldBe(result.Points);
    }

    [Fact]
    public void The_game_ends_after_the_last_round()
    {
        var session = new ChartSession(Library, seed: 11);
        for (var i = 0; i < ChartSession.Rounds; i++)
        {
            session.Guess(Prediction.Unsure);
        }

        session.IsFinished.ShouldBeTrue();
        session.Score.ShouldBe(0, "never committing scores nothing, good or bad");
        Should.Throw<InvalidOperationException>(() => session.Guess(Prediction.Bull));
    }

    [Fact]
    public void The_same_seed_gives_the_same_chart()
    {
        var a = new ChartSession(Library, seed: 2026);
        var b = new ChartSession(Library, seed: 2026);

        (a.Series.Symbol, a.Start).ShouldBe((b.Series.Symbol, b.Start));
    }

    [Fact]
    public void Prices_are_shown_as_an_index_starting_at_100()
    {
        var session = new ChartSession(Library, seed: 3);

        session.Rebase(session.All[0]).Close.ShouldBe(ChartSession.IndexBase);
    }

    [Property(MaxTest = 100)]
    public void Any_game_keeps_its_books_straight(ulong seed, byte[] choices)
    {
        var session = new ChartSession(Library, seed);
        var plays = (choices ?? []).Take(ChartSession.Rounds).Select(c => (Prediction)(c % 5)).ToList();

        foreach (var prediction in plays)
        {
            var result = session.Guess(prediction);
            var before = session.Visible[^2].Close;
            result.Move.ShouldBe(result.Revealed.Close.CompareTo(before) switch { > 0 => Move.Up, < 0 => Move.Down, _ => Move.Flat });
        }

        session.Score.ShouldBe(session.Guesses.Sum(g => g.Points));
        session.Visible.Count.ShouldBe(ChartSession.HistoryCandles + plays.Count);
        session.Visible.Select(session.Rebase).ShouldAllBe(c => c.IsValid, "rebasing keeps every candle's shape");
    }
}
