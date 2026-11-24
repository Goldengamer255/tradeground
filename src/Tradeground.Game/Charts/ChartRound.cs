namespace Tradeground.Game.Charts;

public enum Direction
{
    Up,
    Down,
}

/// <summary>
/// What happened after the call, on the timeframe the player called it on.
/// <see cref="Correct"/> is null if the price ended exactly flat.
/// </summary>
public sealed record CallResult(Timeframe Timeframe, Direction Call, Direction? Actual, bool? Correct, long PriceNow, long EndPrice)
{
    public decimal ChangePercent => Math.Round((EndPrice - PriceNow) * 100m / PriceNow, 2);

    /// <summary>When the call is decided: 20 candles of the chosen timeframe after "now".</summary>
    public TimeSpan Horizon => Timeframe.Length * ChartMoment.FutureCandles;
}

/// <summary>
/// One chart: a market frozen at "now", viewable at every timeframe. The player looks at any
/// zoom level, calls up or down on the one they're viewing, then the future is revealed on all.
/// </summary>
/// <remarks>
/// The future candles never leave the server before the call, so there is nothing to peek at.
/// Which market and which dates stay hidden until after the call; otherwise a player could look
/// up what happened. Not thread-safe; the API serialises access.
/// </remarks>
public sealed class ChartRound(ChartMoment moment)
{
    /// <summary>Displayed prices are rebased so the price at "now" is this value (100.00) on every timeframe.</summary>
    public const long IndexBase = 10_000;

    public ChartMoment Moment { get; } = moment ?? throw new ArgumentNullException(nameof(moment));

    public CallResult? Result { get; private set; }

    public bool IsCalled => Result is not null;

    /// <summary>The history before the call; the whole frame (history and future) after it.</summary>
    public IReadOnlyList<Candle> Visible(Timeframe timeframe)
    {
        var frame = Moment.Frame(timeframe);
        return IsCalled ? frame : frame.Take(ChartMoment.HistoryCandles).ToList();
    }

    public static ChartRound Pick(ChartLibrary library, DeterministicRandom random)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(random);
        return new ChartRound(library.Moments[random.Next(0, library.Moments.Count)]);
    }

    public CallResult Call(Direction call, Timeframe timeframe)
    {
        ArgumentNullException.ThrowIfNull(timeframe);
        if (!Enum.IsDefined(call))
        {
            throw new ArgumentOutOfRangeException(nameof(call));
        }

        if (IsCalled)
        {
            throw new InvalidOperationException("You already called this chart. Start a new one.");
        }

        var end = Moment.Frame(timeframe)[^1].Close;
        Direction? actual = end.CompareTo(Moment.PriceNow) switch
        {
            > 0 => Direction.Up,
            < 0 => Direction.Down,
            _ => null,
        };

        Result = new CallResult(timeframe, call, actual, actual is null ? null : actual == call, Moment.PriceNow, end);
        return Result;
    }

    /// <summary>
    /// Hides the real price level (which would give the market away) by showing every price as an
    /// index where "now" is 100.00. The same base on every timeframe, so all views line up.
    /// </summary>
    public Candle Rebase(Candle candle) => candle with
    {
        Open = Scale(candle.Open),
        High = Scale(candle.High),
        Low = Scale(candle.Low),
        Close = Scale(candle.Close),
    };

    private long Scale(long price) =>
        (long)Math.Round((decimal)price * IndexBase / Moment.PriceNow, MidpointRounding.AwayFromZero);
}

/// <summary>A player's run of charts: the current one, plus right/wrong counts and streaks.</summary>
public sealed class ChartRun(ChartLibrary library, ulong seed)
{
    private readonly DeterministicRandom _random = new(seed);

    public ChartRound? Current { get; private set; }

    public int Right { get; private set; }

    public int Wrong { get; private set; }

    public int Flat { get; private set; }

    public int Streak { get; private set; }

    public int BestStreak { get; private set; }

    /// <summary>Deals a new chart. Skipping an uncalled chart is allowed and doesn't count either way.</summary>
    public ChartRound Next()
    {
        Current = ChartRound.Pick(library, _random);
        return Current;
    }

    public CallResult Call(Direction call, Timeframe timeframe)
    {
        var round = Current ?? throw new InvalidOperationException("Deal a chart first.");
        var result = round.Call(call, timeframe);

        switch (result.Correct)
        {
            case true:
                Right++;
                Streak++;
                BestStreak = Math.Max(BestStreak, Streak);
                break;
            case false:
                Wrong++;
                Streak = 0;
                break;
            default:
                Flat++; // A flat chart neither breaks nor extends a streak.
                break;
        }

        return result;
    }
}
