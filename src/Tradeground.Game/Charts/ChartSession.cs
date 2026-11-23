namespace Tradeground.Game.Charts;

/// <summary>Bull or bear, and how sure. Each choice is a stated probability that the price goes up.</summary>
public enum Prediction
{
    StrongBear,
    Bear,
    Unsure,
    Bull,
    StrongBull,
}

public enum Move
{
    Down,
    Flat,
    Up,
}

/// <summary>
/// A Brier-style scoring rule: points = 100 - (stated% - actual%)² / 25, where actual is 100 if the
/// price went up and 0 if it went down.
/// </summary>
/// <remarks>
/// It is a PROPER scoring rule: your expected score is highest when you say what you really believe.
/// Saying "90% bull" when you think it's a coin flip costs you on average, so bluffing never pays.
/// "Not sure" always scores 0; confident and right scores +96; confident and wrong scores -224.
/// The penalty for overconfidence is the lesson. Integers only, so no rounding surprises.
/// </remarks>
public static class Scoring
{
    public static int ChanceOfUpPercent(Prediction prediction) => prediction switch
    {
        Prediction.StrongBear => 10,
        Prediction.Bear => 30,
        Prediction.Unsure => 50,
        Prediction.Bull => 70,
        Prediction.StrongBull => 90,
        _ => throw new ArgumentOutOfRangeException(nameof(prediction)),
    };

    public static int Points(Prediction prediction, Move move)
    {
        if (move == Move.Flat)
        {
            return 0;
        }

        var actual = move == Move.Up ? 100 : 0;
        var miss = ChanceOfUpPercent(prediction) - actual;
        return 100 - (miss * miss / 25);
    }
}

public sealed record GuessResult(int Round, Prediction Prediction, Move Move, int Points, Candle Revealed);

/// <summary>
/// One solo game of Chart Replay: a random stretch of a real currency chart, shown up to "now".
/// Each round the player predicts the next week, then that week is revealed.
/// </summary>
/// <remarks>
/// The future candles never leave the server until they are revealed, so there is nothing to peek at.
/// Which pair and which dates are hidden until the end: otherwise a player could look up what happened.
/// Not thread-safe; the API serialises access to each session.
/// </remarks>
public sealed class ChartSession
{
    public const int HistoryCandles = 40;
    public const int Rounds = 20;
    public const int TotalCandles = HistoryCandles + Rounds;

    /// <summary>Displayed prices are rebased so the first candle closes at this value (100.00).</summary>
    public const long IndexBase = 10_000;

    private readonly List<GuessResult> _guesses = [];

    public ChartSession(ChartLibrary library, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(library);
        var random = new DeterministicRandom(seed);
        Series = library.Series[random.Next(0, library.Series.Count)];
        Start = random.Next(0, Series.Candles.Count - TotalCandles + 1);
    }

    public ChartSeries Series { get; }

    /// <summary>Index of the first candle of this game within the series.</summary>
    public int Start { get; }

    public int RoundsPlayed => _guesses.Count;

    public bool IsFinished => RoundsPlayed == Rounds;

    public int Score => _guesses.Sum(g => g.Points);

    public IReadOnlyList<GuessResult> Guesses => _guesses;

    /// <summary>Candles the player is allowed to see: the history plus every week revealed so far.</summary>
    public IReadOnlyList<Candle> Visible => Series.Candles.Skip(Start).Take(HistoryCandles + RoundsPlayed).ToList();

    public IReadOnlyList<Candle> All => Series.Candles.Skip(Start).Take(TotalCandles).ToList();

    public GuessResult Guess(Prediction prediction)
    {
        if (!Enum.IsDefined(prediction))
        {
            throw new ArgumentOutOfRangeException(nameof(prediction));
        }

        if (IsFinished)
        {
            throw new InvalidOperationException("This chart is finished. Start a new one.");
        }

        var now = Series.Candles[Start + HistoryCandles + RoundsPlayed - 1];
        var next = Series.Candles[Start + HistoryCandles + RoundsPlayed];
        var move = next.Close.CompareTo(now.Close) switch
        {
            > 0 => Move.Up,
            < 0 => Move.Down,
            _ => Move.Flat,
        };

        var result = new GuessResult(RoundsPlayed + 1, prediction, move, Scoring.Points(prediction, move), next);
        _guesses.Add(result);
        return result;
    }

    /// <summary>
    /// Hides the real price level (which would give away the pair) by showing every price as an
    /// index where the first candle closes at 100.00. Shapes and percentage moves are unchanged.
    /// </summary>
    public Candle Rebase(Candle candle)
    {
        var baseClose = Series.Candles[Start].Close;
        return candle with
        {
            Open = Scale(candle.Open),
            High = Scale(candle.High),
            Low = Scale(candle.Low),
            Close = Scale(candle.Close),
        };

        long Scale(long price) => (long)Math.Round((decimal)price * IndexBase / baseClose, MidpointRounding.AwayFromZero);
    }
}
