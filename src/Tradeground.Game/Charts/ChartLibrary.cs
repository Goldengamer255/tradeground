using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tradeground.Game.Charts;

/// <summary>
/// The historical charts shipped inside this assembly, checked on load.
/// Regenerate the data files with tools/Tradeground.ChartData.
/// </summary>
public sealed class ChartLibrary
{
    public static readonly IReadOnlyList<string> ResourceNames =
    [
        "Tradeground.Game.Charts.fx-weekly.json",
        "Tradeground.Game.Charts.crypto-intraday.json",
    ];

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new CandleArrayConverter() },
    };

    public ChartLibrary(IEnumerable<ChartDataset> datasets, int minimumSegmentLength)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        var all = datasets.ToList();
        foreach (var series in all.SelectMany(d => d.Series))
        {
            Validate(series, minimumSegmentLength);
        }

        Sources = all.Select(d => d.Source).ToList();
        Series = all.SelectMany(d => d.Series).ToList();
        Timeframes = Timeframe.All.Where(t => Series.Any(s => s.Timeframe == t.Id)).ToList();
    }

    public IReadOnlyList<string> Sources { get; }

    public IReadOnlyList<ChartSeries> Series { get; }

    /// <summary>Timeframes that have at least one market, shortest first.</summary>
    public IReadOnlyList<Timeframe> Timeframes { get; }

    public IReadOnlyList<ChartSeries> SeriesFor(Timeframe timeframe) =>
        Series.Where(s => s.Timeframe == timeframe.Id).ToList();

    public static ChartLibrary LoadBuiltIn(int minimumSegmentLength = ChartRound.TotalCandles)
    {
        var assembly = typeof(ChartLibrary).Assembly;
        var datasets = ResourceNames.Select(name =>
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing resource {name}.");
            return JsonSerializer.Deserialize<ChartDataset>(stream, JsonOptions)
                ?? throw new InvalidOperationException($"Resource {name} is empty.");
        });
        return new ChartLibrary(datasets, minimumSegmentLength);
    }

    /// <summary>Bad data should stop the app at startup, not surprise a player mid-game.</summary>
    private static void Validate(ChartSeries series, int minimumSegmentLength)
    {
        var where = $"{series.Symbol} {series.Timeframe}";
        if (Timeframe.Find(series.Timeframe) is null)
        {
            throw new InvalidOperationException($"{where}: unknown timeframe.");
        }

        if (series.Decimals is < 0 or > 8)
        {
            throw new InvalidOperationException($"{where}: decimals must be between 0 and 8.");
        }

        if (series.Segments.Count == 0)
        {
            throw new InvalidOperationException($"{where}: has no candles.");
        }

        foreach (var segment in series.Segments)
        {
            if (segment.Count < minimumSegmentLength)
            {
                throw new InvalidOperationException($"{where}: every segment needs at least {minimumSegmentLength} candles.");
            }

            for (var i = 0; i < segment.Count; i++)
            {
                if (!segment[i].IsValid)
                {
                    throw new InvalidOperationException($"{where}: candle at {segment[i].OpenTime:u} has impossible prices.");
                }

                if (i > 0 && segment[i].OpenTime <= segment[i - 1].OpenTime)
                {
                    throw new InvalidOperationException($"{where}: candles are out of order at {segment[i].OpenTime:u}.");
                }
            }
        }
    }
}

public sealed record ChartDataset(string Source, IReadOnlyList<ChartSeries> Series);

/// <summary>Stores a candle as [unixSeconds, open, high, low, close]: about a third of the size of an object.</summary>
public sealed class CandleArrayConverter : JsonConverter<Candle>
{
    public override Candle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("A candle must be an array.");
        }

        var values = new long[5];
        for (var i = 0; i < values.Length; i++)
        {
            reader.Read();
            values[i] = reader.GetInt64();
        }

        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("A candle has exactly five values.");
        }

        return new Candle(DateTimeOffset.FromUnixTimeSeconds(values[0]), values[1], values[2], values[3], values[4]);
    }

    public override void Write(Utf8JsonWriter writer, Candle value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartArray();
        writer.WriteNumberValue(value.OpenTime.ToUnixTimeSeconds());
        writer.WriteNumberValue(value.Open);
        writer.WriteNumberValue(value.High);
        writer.WriteNumberValue(value.Low);
        writer.WriteNumberValue(value.Close);
        writer.WriteEndArray();
    }
}
