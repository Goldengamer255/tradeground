namespace Tradeground.Game.Charts;

/// <summary>
/// One candle. Like everything with money in Tradeground, prices are integers: the quote times
/// 10^decimals (BTC/USDT 64,250.17 with 2 decimals is 6425017).
/// </summary>
public readonly record struct Candle(DateTimeOffset OpenTime, long Open, long High, long Low, long Close)
{
    public bool IsValid => Low > 0 && Low <= Math.Min(Open, Close) && High >= Math.Max(Open, Close);
}

/// <summary>A chart timeframe: how much time one candle covers.</summary>
public sealed record Timeframe(string Id, string Label, TimeSpan Length)
{
    public static readonly Timeframe M1 = new("1m", "1 minute", TimeSpan.FromMinutes(1));
    public static readonly Timeframe M15 = new("15m", "15 minutes", TimeSpan.FromMinutes(15));
    public static readonly Timeframe M30 = new("30m", "30 minutes", TimeSpan.FromMinutes(30));
    public static readonly Timeframe H1 = new("1h", "1 hour", TimeSpan.FromHours(1));
    public static readonly Timeframe H4 = new("4h", "4 hours", TimeSpan.FromHours(4));
    public static readonly Timeframe W1 = new("1w", "1 week", TimeSpan.FromDays(7));

    /// <summary>The zoom levels every Up or Down chart has, shortest first.</summary>
    public static readonly IReadOnlyList<Timeframe> Intraday = [M1, M15, M30, H1, H4];

    public static Timeframe? Find(string? id) => Intraday.FirstOrDefault(t => t.Id == id);
}

/// <summary>
/// One market frozen at one moment, seen at every timeframe. Each frame holds the candles just
/// before <see cref="Now"/> (the history) and just after it (the future), so switching timeframe
/// zooms in or out on the SAME chart instead of showing a different one.
/// </summary>
public sealed record ChartMoment(
    string Symbol,
    string Name,
    int Decimals,
    DateTimeOffset Now,
    IReadOnlyDictionary<string, IReadOnlyList<Candle>> Frames)
{
    public const int HistoryCandles = 60;
    public const int FutureCandles = 20;
    public const int CandlesPerFrame = HistoryCandles + FutureCandles;

    public IReadOnlyList<Candle> Frame(Timeframe timeframe) => Frames[timeframe.Id];

    /// <summary>The price at "now": the close of the last history candle. Identical on every timeframe.</summary>
    public long PriceNow => Frame(Timeframe.M1)[HistoryCandles - 1].Close;
}

public static class WeeklyCandles
{
    /// <summary>
    /// Groups daily prices into Monday-to-Sunday candles: open = first day, close = last day,
    /// high and low = the extremes of the week. Days must be in date order.
    /// </summary>
    /// <remarks>
    /// Used for the Federal Reserve forex data, which has one price per day (the noon buying rate
    /// in New York), so these candles show the range of daily rates, not every tick.
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

            if (week is { } current && current.OpenTime == start)
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

    /// <summary>Monday 00:00 UTC of the week containing <paramref name="date"/>.</summary>
    public static DateTimeOffset StartOfWeek(DateOnly date)
    {
        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        return new DateTimeOffset(monday.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }
}
