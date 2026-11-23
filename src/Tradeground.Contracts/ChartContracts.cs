namespace Tradeground.Contracts;

// Up or Down: the shapes of the chart API.

public enum Direction
{
    Up,
    Down,
}

public sealed record TimeframeRequest(string? Timeframe);

public sealed record CallRequest(Direction Direction, string? Timeframe);

public sealed record ChartStarted(string Key, ChartView Chart);

public sealed record TimeframeView(string Id, string Label);

/// <summary>A candle as players see it: rebased so "now" is 100.00 (10000), with no time.</summary>
public sealed record CandleView(long Open, long High, long Low, long Close);

public sealed record RunStats(int Right, int Wrong, int Flat, int Streak, int BestStreak);

/// <summary>The call, including the timeframe it was made on (which sets how far ahead it looked).</summary>
public sealed record CallView(string Timeframe, Direction Call, Direction? Actual, bool? Correct, decimal ChangePercent);

/// <summary>
/// The current chart at one timeframe. Before the call it holds only the history, as an index with
/// no times; <see cref="Result"/> and <see cref="Reveal"/> stay null, so the market, the dates and
/// the future can't be read from the network traffic.
/// </summary>
public sealed record ChartView(
    string Timeframe,
    int HistoryCandles,
    int Horizon,
    IReadOnlyList<CandleView> Candles,
    CallView? Result,
    ChartReveal? Reveal,
    RunStats Stats);

/// <summary>The answer: which market and when, with the real prices at "now" and when the call was decided.</summary>
public sealed record ChartReveal(
    string Symbol,
    string Name,
    DateTimeOffset Now,
    DateTimeOffset DecidedAt,
    string PriceNow,
    string EndPrice);

/// <summary>A real move from the chart library, for the home page's ticker tape.</summary>
public sealed record ChartHighlight(string Symbol, DateTimeOffset When, string Timeframe, decimal ChangePercent);
