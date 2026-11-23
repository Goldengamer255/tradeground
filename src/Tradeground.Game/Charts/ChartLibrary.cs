using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tradeground.Game.Charts;

/// <summary>
/// The historical charts shipped inside this assembly (Charts/fx-weekly.json), checked on load.
/// Regenerate the file with tools/Tradeground.ChartData.
/// </summary>
public sealed class ChartLibrary
{
    public const string ResourceName = "Tradeground.Game.Charts.fx-weekly.json";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new CandleArrayConverter() },
    };

    public ChartLibrary(ChartDataset dataset, int minimumCandles)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        Source = dataset.Source;
        foreach (var series in dataset.Series)
        {
            Validate(series, minimumCandles);
        }

        Series = dataset.Series;
    }

    public string Source { get; }

    public IReadOnlyList<ChartSeries> Series { get; }

    public static ChartLibrary LoadBuiltIn(int minimumCandles = ChartSession.TotalCandles)
    {
        using var stream = typeof(ChartLibrary).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing resource {ResourceName}.");
        var dataset = JsonSerializer.Deserialize<ChartDataset>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Resource {ResourceName} is empty.");
        return new ChartLibrary(dataset, minimumCandles);
    }

    /// <summary>Bad data should stop the app at startup, not surprise a player mid-game.</summary>
    private static void Validate(ChartSeries series, int minimumCandles)
    {
        if (series.Decimals is < 0 or > 8)
        {
            throw new InvalidOperationException($"{series.Symbol}: decimals must be between 0 and 8.");
        }

        if (series.Candles.Count < minimumCandles)
        {
            throw new InvalidOperationException($"{series.Symbol}: needs at least {minimumCandles} candles.");
        }

        for (var i = 0; i < series.Candles.Count; i++)
        {
            var candle = series.Candles[i];
            if (!candle.IsValid)
            {
                throw new InvalidOperationException($"{series.Symbol}: candle for {candle.WeekStart} has impossible prices.");
            }

            if (i > 0 && candle.WeekStart <= series.Candles[i - 1].WeekStart)
            {
                throw new InvalidOperationException($"{series.Symbol}: candles are out of order at {candle.WeekStart}.");
            }
        }
    }
}

public sealed record ChartDataset(string Source, IReadOnlyList<ChartSeries> Series);

/// <summary>Stores a candle as ["2024-01-01", open, high, low, close]: about a third of the size of an object.</summary>
public sealed class CandleArrayConverter : JsonConverter<Candle>
{
    public override Candle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("A candle must be an array.");
        }

        reader.Read();
        var week = DateOnly.ParseExact(reader.GetString() ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var prices = new long[4];
        for (var i = 0; i < prices.Length; i++)
        {
            reader.Read();
            prices[i] = reader.GetInt64();
        }

        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("A candle has exactly five values.");
        }

        return new Candle(week, prices[0], prices[1], prices[2], prices[3]);
    }

    public override void Write(Utf8JsonWriter writer, Candle value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartArray();
        writer.WriteStringValue(value.WeekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteNumberValue(value.Open);
        writer.WriteNumberValue(value.High);
        writer.WriteNumberValue(value.Low);
        writer.WriteNumberValue(value.Close);
        writer.WriteEndArray();
    }
}
