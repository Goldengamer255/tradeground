namespace Tradeground.Game.Charts;

public enum Direction
{
    Up,
    Down,
}

/// <summary>What happened after the call. <see cref="Correct"/> is null if the price ended exactly flat.</summary>
public sealed record CallResult(Direction Call, Direction? Actual, bool? Correct, long DecisionClose, long EndClose)
{
    public decimal ChangePercent => Math.Round((EndClose - DecisionClose) * 100m / DecisionClose, 2);
}

/// <summary>
/// One chart: some history up to "now", one call (up or down), then the next stretch is revealed.
/// The call is right if the price at the end of the stretch is above (or below) the price at the call.
/// </summary>
/// <remarks>
/// The future candles never leave the server before the call, so there is nothing to peek at.
/// Which market and which dates stay hidden until after the call; otherwise a player could look
/// up what happened. Not thread-safe; the API serialises access.
/// </remarks>
public sealed class ChartRound
{
    public const int HistoryCandles = 60;
    public const int Horizon = 20;
    public const int TotalCandles = HistoryCandles + Horizon;

    /// <summary>Displayed prices are rebased so the first candle closes at this value (100.00).</summary>
    public const long IndexBase = 10_000;

    private readonly IReadOnlyList<Candle> _candles;

    public ChartRound(ChartSeries series, Timeframe timeframe, IReadOnlyList<Candle> segment, int start)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, segment.Count - TotalCandles);
        Series = series;
        Timeframe = timeframe;
        _candles = segment.Skip(start).Take(TotalCandles).ToList();
    }

    public ChartSeries Series { get; }

    public Timeframe Timeframe { get; }

    public CallResult? Result { get; private set; }

    public bool IsCalled => Result is not null;

    /// <summary>The history before the call; the whole chart after it.</summary>
    public IReadOnlyList<Candle> Visible => IsCalled ? _candles : _candles.Take(HistoryCandles).ToList();

    /// <summary>Picks a random market on this timeframe and a random stretch of its history.</summary>
    public static ChartRound Pick(ChartLibrary library, Timeframe timeframe, DeterministicRandom random)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(timeframe);
        ArgumentNullException.ThrowIfNull(random);

        var markets = library.SeriesFor(timeframe);
        if (markets.Count == 0)
        {
            throw new ArgumentException($"No charts for timeframe {timeframe.Id}.", nameof(timeframe));
        }

        var series = markets[random.Next(0, markets.Count)];
        var segment = series.Segments[random.Next(0, series.Segments.Count)];
        return new ChartRound(series, timeframe, segment, random.Next(0, segment.Count - TotalCandles + 1));
    }

    public CallResult Call(Direction call)
    {
        if (!Enum.IsDefined(call))
        {
            throw new ArgumentOutOfRangeException(nameof(call));
        }

        if (IsCalled)
        {
            throw new InvalidOperationException("You already called this chart. Start a new one.");
        }

        var decision = _candles[HistoryCandles - 1].Close;
        var end = _candles[^1].Close;
        Direction? actual = end.CompareTo(decision) switch
        {
            > 0 => Direction.Up,
            < 0 => Direction.Down,
            _ => null,
        };

        Result = new CallResult(call, actual, actual is null ? null : actual == call, decision, end);
        return Result;
    }

    /// <summary>
    /// Hides the real price level (which would give the market away) by showing every price as an
    /// index where the first candle closes at 100.00. Shapes and percentage moves are unchanged.
    /// </summary>
    public Candle Rebase(Candle candle)
    {
        var baseClose = _candles[0].Close;
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
    public ChartRound Next(Timeframe timeframe)
    {
        Current = ChartRound.Pick(library, timeframe, _random);
        return Current;
    }

    public CallResult Call(Direction call)
    {
        var round = Current ?? throw new InvalidOperationException("Deal a chart first.");
        var result = round.Call(call);

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
