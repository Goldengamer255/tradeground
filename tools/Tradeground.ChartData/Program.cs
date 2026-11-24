// Builds the chart data Tradeground plays on. Output files are committed, so the game never calls
// an outside service while people play.
//
//   crypto <download cache folder> <output json>
//       Up or Down's chart moments: BTC, ETH, BNB and XRP at fixed "now" times, each seen at
//       1m, 15m, 30m, 1h and 4h. From Binance public data (data.binance.vision), CC BY-NC-SA 4.0.
//       Downloads what it needs into the cache folder.
//
//   fx <folder of FRED CSVs> <output json>
//       Weekly forex candles from Federal Reserve H.10 daily rates (public domain), kept for Trade
//       Through History. Download each series first, e.g.
//       https://fred.stlouisfed.org/graph/fredgraph.csv?id=DEXUSEU&cosd=2000-01-01 saved as <folder>/DEXUSEU.csv.

using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Tradeground.Game.Charts;

if (args.Length != 3 || args[0] is not ("fx" or "crypto"))
{
    Console.Error.WriteLine("Usage: crypto <download cache folder> <output json>");
    Console.Error.WriteLine("       fx <folder of FRED CSVs> <output json>");
    return 1;
}

object dataset = args[0] == "crypto" ? await BuildMoments(args[1]) : BuildForex(args[1]);

await using (var output = File.Create(args[2]))
{
    await JsonSerializer.SerializeAsync(output, dataset, dataset.GetType(), ChartLibrary.JsonOptions);
}

Console.WriteLine($"Wrote {args[2]} ({new FileInfo(args[2]).Length / 1024} KB)");
return 0;

// ---------- Crypto moments: Binance public data ----------

static async Task<MomentDataset> BuildMoments(string cache)
{
    (string Pair, string Symbol, string Name, int Decimals)[] markets =
    [
        ("BTCUSDT", "BTC/USDT", "Bitcoin / Tether", 2),
        ("ETHUSDT", "ETH/USDT", "Ether / Tether", 2),
        ("BNBUSDT", "BNB/USDT", "BNB / Tether", 4),
        ("XRPUSDT", "XRP/USDT", "XRP / Tether", 5),
    ];

    // Ten months spread over calm and wild markets. Every "now" is on the 12th, so the 4h chart's
    // 10 days of history and 3 days of future both stay inside the month's file.
    string[] months = ["2019-06", "2020-03", "2021-01", "2021-05", "2022-06", "2022-11", "2023-10", "2024-03", "2025-04", "2026-02"];

    // On a 4-hour boundary, so "now" is the start of a candle on every timeframe.
    int[] hours = [4, 12, 20];

    Directory.CreateDirectory(cache);
    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Tradeground-ChartData/1.0");

    var moments = new List<ChartMoment>();
    foreach (var (pair, symbol, name, decimals) in markets)
    {
        foreach (var month in months)
        {
            var frames = new Dictionary<string, List<Candle>>();
            foreach (var timeframe in Timeframe.Intraday)
            {
                var file = timeframe == Timeframe.M1
                    ? $"daily/klines/{pair}/1m/{pair}-1m-{month}-12.zip"
                    : $"monthly/klines/{pair}/{timeframe.Id}/{pair}-{timeframe.Id}-{month}.zip";
                frames[timeframe.Id] = await ReadBinanceKlines(http, cache, file, decimals);
            }

            foreach (var hour in hours)
            {
                var now = DateTimeOffset.ParseExact($"{month}-12 {hour:00}:00", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                if (Cut(frames, now) is not { } cut)
                {
                    Console.WriteLine($"  skipped {symbol} {now:u}: not enough candles around it");
                    continue;
                }

                var moment = new ChartMoment(symbol, name, decimals, now, cut);
                try
                {
                    ChartLibrary.Validate(moment);
                    moments.Add(moment);
                }
                catch (InvalidOperationException ex)
                {
                    // Exchanges occasionally pause, leaving a gap; such moments are left out.
                    Console.WriteLine($"  skipped: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"{symbol}: {moments.Count(m => m.Symbol == symbol)} moments");
    }

    return new MomentDataset(
        "Binance public market data (data.binance.vision), licensed CC BY-NC-SA 4.0. This file is a derived work, shared under the same license.",
        moments);
}

/// <summary>For every timeframe: the 60 candles before "now" and the 20 from "now" on.</summary>
static Dictionary<string, IReadOnlyList<Candle>>? Cut(Dictionary<string, List<Candle>> frames, DateTimeOffset now)
{
    var cut = new Dictionary<string, IReadOnlyList<Candle>>();
    foreach (var (id, candles) in frames)
    {
        var at = candles.FindIndex(c => c.OpenTime == now);
        if (at < ChartMoment.HistoryCandles || at + ChartMoment.FutureCandles > candles.Count)
        {
            return null;
        }

        cut[id] = candles.GetRange(at - ChartMoment.HistoryCandles, ChartMoment.CandlesPerFrame);
    }

    return cut;
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

// ---------- Forex: Federal Reserve H.10 ----------

static ForexDataset BuildForex(string folder)
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
        if (candles.Any(c => !c.IsValid))
        {
            throw new InvalidDataException($"{p.Symbol}: a weekly candle has impossible prices.");
        }

        Console.WriteLine($"{p.Symbol}: {daily.Count} days -> {candles.Count} weekly candles");
        return new ForexSeries(p.Symbol, p.Name, Timeframe.W1.Id, p.Decimals, candles);
    }).ToList();

    return new ForexDataset(
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

internal sealed record ForexSeries(string Symbol, string Name, string Timeframe, int Decimals, IReadOnlyList<Candle> Candles);

internal sealed record ForexDataset(string Source, IReadOnlyList<ForexSeries> Series);
