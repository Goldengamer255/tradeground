namespace Tradeground.Game.Charts;

/// <summary>
/// One week of prices. Like everything with money in Tradeground, prices are integers:
/// the quote times 10^decimals (EUR/USD 1.0850 with 4 decimals is 10850).
/// </summary>
public readonly record struct Candle(DateOnly WeekStart, long Open, long High, long Low, long Close)
{
    public bool IsValid => Low > 0 && Low <= Math.Min(Open, Close) && High >= Math.Max(Open, Close);
}

/// <summary>A currency pair and its weekly history, oldest first.</summary>
public sealed record ChartSeries(string Symbol, string Name, int Decimals, IReadOnlyList<Candle> Candles);

public static class WeeklyCandles
{
    /// <summary>
    /// Groups daily prices into Monday-to-Sunday candles: open = first day, close = last day,
    /// high and low = the extremes of the week. Days must be in date order.
    /// </summary>
    /// <remarks>
    /// The Fed publishes one rate per day (the noon buying rate in New York), so these candles
    /// show the range of daily rates, not every tick: real intraday wicks would be longer.
    /// </remarks>
    public static IReadOnlyList<Candle> FromDaily(IEnumerable<(DateOnly Date, long Price)> daily)
    {
        ArgumentNullException.ThrowIfNull(daily);
        var candles = new List<Candle>();
        Candle? week = null;
        var previousDate = DateOnly.MinValue;

        foreach (var (date, price) in daily)
        {
            if (date <= previousDate)
            {
                throw new ArgumentException($"Daily prices must be in date order; {date} came after {previousDate}.", nameof(daily));
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
            previousDate = date;
            var start = StartOfWeek(date);

            if (week is { } current && current.WeekStart == start)
            {
                week = current with
                {
                    High = Math.Max(current.High, price),
                    Low = Math.Min(current.Low, price),
                    Close = price,
                };
            }
            else
            {
                if (week is { } finished)
                {
                    candles.Add(finished);
                }

                week = new Candle(start, price, price, price, price);
            }
        }

        if (week is { } last)
        {
            candles.Add(last);
        }

        return candles;
    }

    public static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
