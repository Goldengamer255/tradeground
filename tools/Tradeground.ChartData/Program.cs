// Builds the chart data Chart Replay plays on. The output files are committed, so the game never
// calls an outside service while people play.
//
//   fx     <folder of FRED CSVs> <output json>
//       Weekly forex candles from Federal Reserve H.10 daily rates (public domain). Download each
//       series first, e.g. https://fred.stlouisfed.org/graph/fredgraph.csv?id=DEXUSEU&cosd=2000-01-01
//       saved as <folder>/DEXUSEU.csv.
//
//   crypto <download cache folder> <output json>
//       1m/15m/30m/1h/4h crypto candles from Binance public data (data.binance.vision),
//       CC BY-NC-SA 4.0. Downloads what it needs into the cache folder.

using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Tradeground.Game;
using Tradeground.Game.Charts;

if (args.Length != 3 || args[0] is not ("fx" or "crypto"))
{
    Console.Error.WriteLine("Usage: fx <folder of FRED CSVs> <output json>");
    Console.Error.WriteLine("       crypto <download cache folder> <output json>");
    return 1;
}

var dataset = args[0] == "fx" ? BuildForex(args[1]) : await BuildCrypto(args[1]);

// Validates exactly as the game will at startup, so a bad file is caught here, not in production.
_ = new ChartLibrary([dataset], ChartRound.TotalCandles);

await using (var output = File.Create(args[2]))
{
    await JsonSerializer.SerializeAsync(output, dataset, ChartLibrary.JsonOptions);
}

Console.WriteLine($"Wrote {args[2]} ({new FileInfo(args[2]).Length / 1024} KB)");
return 0;

// ---------- Forex: Federal Reserve H.10 ----------

static ChartDataset BuildForex(string folder)
{
    // FRED id, symbol, name, decimals. Each pair is quoted the way traders quote it.
    (string FredId, string Symbol, string Name, int Decimals)[] pairs =
    [
        ("DEXUSEU", "EUR/USD", "Euro / US dollar", 4),
        ("DEXUSUK", "GBP/USD", "British pound / US dollar", 4),
        ("DEXJPUS", "USD/JPY", "US dollar / Japanese yen", 2),
        ("DEXUSAL", "AUD/USD", "Australian dollar / US dollar", 4),
        ("DEXCAUS", "USD/CAD", "US dollar / Canadian dollar", 4),
        ("DEXSZUS", "USD/CHF", "US dollar / Swiss franc", 4),
    ];

    var series = pairs.Select(p =>
    {
        var daily = ReadFredDaily(Path.Combine(folder, $"{p.FredId}.csv"), p.Decimals);
        var candles = WeeklyCandles.FromDaily(daily);
        Console.WriteLine($"{p.Symbol}: {daily.Count} days -> {candles.Count} weekly candles");
        return new ChartSeries(p.Symbol, p.Name, Timeframe.W1.Id, p.Decimals, [candles]);
    }).ToList();

    return new ChartDataset(
        "Board of Governors of the Federal Reserve System (US), H.10 Foreign Exchange Rates, retrieved from FRED, Federal Reserve Bank of St. Louis. Public domain.",
        series);
}

static List<(DateOnly Date, long Price)> ReadFredDaily(string path, int decimals)
{
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
        rows.Add((date, ToTicks(parts[1], decimals, $"{path} {date}")));
    }

    return rows;
}

// ---------- Crypto: Binance public data ----------

static async Task<ChartDataset> BuildCrypto(string cache)
{
    (string Pair, string Symbol, string Name, int Decimals)[] markets =
    [
        ("BTCUSDT", "BTC/USDT", "Bitcoin / Tether", 2),
        ("ETHUSDT", "ETH/USDT", "Ether / Tether", 2),
        ("BNBUSDT", "BNB/USDT", "BNB / Tether", 4),
        ("XRPUSDT", "XRP/USDT", "XRP / Tether", 5),
    ];

    // Ten months spread over calm and wild markets. 1m uses one day from each month (a day of
    // 1-minute candles is plenty); the other timeframes use the whole month.
    string[] months = ["2019-06", "2020-03", "2021-01", "2021-05", "2022-06", "2022-11", "2023-10", "2024-03", "2025-04", "2026-02"];
    const int SegmentLength = 160;

    Directory.CreateDirectory(cache);
    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Tradeground-ChartData/1.0");

    var series = new List<ChartSeries>();
    foreach (var (pair, symbol, name, decimals) in markets)
    {
        foreach (var timeframe in Timeframe.All.Where(t => t != Timeframe.W1))
        {
            var segments = new List<IReadOnlyList<Candle>>();
            foreach (var month in months)
            {
                var file = timeframe == Timeframe.M1
                    ? $"daily/klines/{pair}/1m/{pair}-1m-{month}-12.zip"
                    : $"monthly/klines/{pair}/{timeframe.Id}/{pair}-{timeframe.Id}-{month}.zip";
                var candles = await ReadBinanceKlines(http, cache, file, decimals);

                // A fixed seed per file: the same inputs always produce the same dataset.
                var random = new DeterministicRandom((ulong)(uint)StableHash(file));
                var start = random.Next(0, candles.Count - SegmentLength + 1);
                segments.Add(candles.Skip(start).Take(SegmentLength).ToList());
            }

            series.Add(new ChartSeries(symbol, name, timeframe.Id, decimals, segments));
            Console.WriteLine($"{symbol} {timeframe.Id}: {segments.Count} segments of {SegmentLength}");
        }
    }

    return new ChartDataset(
        "Binance public market data (data.binance.vision), licensed CC BY-NC-SA 4.0. This file is a derived work, shared under the same license.",
        series);
}

static async Task<List<Candle>> ReadBinanceKlines(HttpClient http, string cache, string file, int decimals)
{
    var local = Path.Combine(cache, file.Replace('/', '_'));
    if (!File.Exists(local))
    {
        var bytes = await http.GetByteArrayAsync(new Uri($"https://data.binance.vision/data/spot/{file}"));
        await File.WriteAllBytesAsync(local, bytes);
    }

    using var zip = ZipFile.OpenRead(local);
    using var reader = new StreamReader(zip.Entries.Single().Open());
    var candles = new List<Candle>();
    while (await reader.ReadLineAsync() is { } line)
    {
        // Columns: open time, open, high, low, close, volume, ... Some files start with a header row.
        var parts = line.Split(',');
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var openTime))
        {
            continue;
        }

        // Binance switched spot files from milliseconds to microseconds in 2025.
        var seconds = openTime >= 100_000_000_000_000 ? openTime / 1_000_000 : openTime / 1_000;
        var where = $"{file} {openTime}";
        candles.Add(new Candle(
            DateTimeOffset.FromUnixTimeSeconds(seconds),
            ToTicks(parts[1], decimals, where),
            ToTicks(parts[2], decimals, where),
            ToTicks(parts[3], decimals, where),
            ToTicks(parts[4], decimals, where)));
    }

    return candles;
}

// ---------- Shared ----------

/// <summary>"1.0850" with 4 decimals -> 10850. Refuses to round: a price that doesn't fit is a bug.</summary>
static long ToTicks(string text, int decimals, string where)
{
    var quote = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
    var ticks = quote * (decimal)Math.Pow(10, decimals);
    if (ticks != decimal.Truncate(ticks))
    {
        throw new InvalidDataException($"{where}: {text} has more than {decimals} decimals.");
    }

    return (long)ticks;
}

/// <summary>string.GetHashCode changes every run in .NET; this one doesn't (FNV-1a).</summary>
static int StableHash(string text)
{
    unchecked
    {
        var hash = (int)2166136261;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619;
        }

        return hash;
    }
}
