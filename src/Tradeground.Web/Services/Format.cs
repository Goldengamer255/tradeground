using System.Globalization;

namespace Tradeground.Web.Services;

/// <summary>How numbers, durations and dates are written on screen. Pure functions, so they're unit tested.</summary>
public static class Format
{
    /// <summary>A real minus sign (−), not a hyphen: it lines up with the plus sign in number columns.</summary>
    public const char Minus = '−';

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>12345 -> "12,345"; -5 -> "−5"; 34.5 -> "34.5".</summary>
    public static string Number(decimal value)
    {
        var abs = Math.Abs(value);
        var text = abs == decimal.Truncate(abs) ? abs.ToString("N0", Invariant) : abs.ToString("N1", Invariant);
        return value < 0 ? Minus + text : text;
    }

    /// <summary>Always shows the sign: "+3", "−3", "0".</summary>
    public static string Signed(decimal value) => value > 0 ? "+" + Number(value) : Number(value);

    /// <summary>A rebased chart price (10000 = 100.00) as shown on the axis: "100.0".</summary>
    public static string Index(decimal rebased) => (rebased / 100m).ToString("0.0", Invariant);

    /// <summary>A percentage change with sign: "+1.24%".</summary>
    public static string Percent(decimal value) =>
        (value > 0 ? "+" : value < 0 ? Minus.ToString() : string.Empty) + Math.Abs(value).ToString("0.00", Invariant) + "%";

    /// <summary>The size of a move, without a sign: -1.236 -> "1.24%".</summary>
    public static string PercentSize(decimal value) => Math.Abs(value).ToString("0.00", Invariant) + "%";

    /// <summary>How long N candles of a timeframe last, in words: 20 x 15m -> "5 hours".</summary>
    public static string Duration(string timeframe, int candles)
    {
        var minutes = timeframe switch
        {
            "1m" => 1,
            "15m" => 15,
            "30m" => 30,
            "1h" => 60,
            "4h" => 240,
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unknown timeframe."),
        } * candles;

        if (minutes < 60)
        {
            return Plural(minutes, "minute");
        }

        var hours = minutes / 60;
        if (hours < 48)
        {
            return Plural(hours, "hour");
        }

        var days = hours / 24;
        var rest = hours % 24;
        return rest == 0 ? Plural(days, "day") : $"{Plural(days, "day")} and {Plural(rest, "hour")}";
    }

    public static string Plural(long count, string word) => $"{count.ToString(Invariant)} {(count == 1 ? word : word + "s")}";

    /// <summary>"12 Mar 2024, 04:00 UTC". Always UTC, so everyone sees the same time.</summary>
    public static string When(DateTimeOffset when) => when.UtcDateTime.ToString("d MMM yyyy, HH:mm", Invariant) + " UTC";

    /// <summary>"Mar 2024", for the ticker tape.</summary>
    public static string Month(DateTimeOffset when) => when.UtcDateTime.ToString("MMM yyyy", Invariant);
}
