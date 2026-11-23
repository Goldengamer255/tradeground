// Turns Federal Reserve daily exchange rates into the weekly candles Chart Replay plays on.
//
// 1. Download each series as CSV from FRED (public domain, citation requested), for example
//    https://fred.stlouisfed.org/graph/fredgraph.csv?id=DEXUSEU&cosd=2000-01-01
//    and save it as <folder>/DEXUSEU.csv.
// 2. dotnet run --project tools/Tradeground.ChartData -- <folder> src/Tradeground.Game/Charts/fx-weekly.json
//
// The output is committed, so the game never calls an outside service while people play.

using System.Globalization;
using System.Text.Json;
using Tradeground.Game.Charts;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: <folder with FRED CSV files> <output json>");
    return 1;
}

// FRED id, market symbol, display name, decimals. Each pair is quoted the way traders quote it.
(string FredId, string Symbol, string Name, int Decimals)[] pairs =
[
    ("DEXUSEU", "EURUSD", "Euro / US dollar", 4),
    ("DEXUSUK", "GBPUSD", "British pound / US dollar", 4),
    ("DEXJPUS", "USDJPY", "US dollar / Japanese yen", 2),
    ("DEXUSAL", "AUDUSD", "Australian dollar / US dollar", 4),
    ("DEXCAUS", "USDCAD", "US dollar / Canadian dollar", 4),
    ("DEXSZUS", "USDCHF", "US dollar / Swiss franc", 4),
];

var series = new List<ChartSeries>();
foreach (var (fredId, symbol, name, decimals) in pairs)
{
    var daily = ReadDaily(Path.Combine(args[0], $"{fredId}.csv"), decimals);
    var candles = WeeklyCandles.FromDaily(daily);
    series.Add(new ChartSeries(symbol, name, decimals, candles));
    Console.WriteLine($"{symbol}: {daily.Count} days -> {candles.Count} weekly candles, {candles[0].WeekStart} to {candles[^1].WeekStart}");
}

var dataset = new ChartDataset(
    "Board of Governors of the Federal Reserve System (US), H.10 Foreign Exchange Rates, retrieved from FRED, Federal Reserve Bank of St. Louis. Public domain.",
    series);

// Validates exactly as the game will at startup, so a bad file is caught here, not in production.
_ = new ChartLibrary(dataset, ChartSession.TotalCandles);

await using var output = File.Create(args[1]);
await JsonSerializer.SerializeAsync(output, dataset, ChartLibrary.JsonOptions);
Console.WriteLine($"Wrote {args[1]}");
return 0;

static List<(DateOnly Date, long Price)> ReadDaily(string path, int decimals)
{
    var scale = (decimal)Math.Pow(10, decimals);
    var rows = new List<(DateOnly, long)>();
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var parts = line.Split(',');

        // Holidays have no rate: FRED leaves the value empty (older files use ".").
        if (parts.Length != 2 || parts[1] is "" or ".")
        {
            continue;
        }

        var date = DateOnly.ParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var quote = decimal.Parse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture);
        var ticks = quote * scale;
        if (ticks != decimal.Truncate(ticks))
        {
            throw new InvalidDataException($"{path} {date}: {quote} has more than {decimals} decimals.");
        }

        rows.Add((date, (long)ticks));
    }

    return rows;
}
