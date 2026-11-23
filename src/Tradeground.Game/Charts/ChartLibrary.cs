using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tradeground.Game.Charts;

/// <summary>
/// The chart moments shipped inside this assembly (Charts/crypto-moments.json), checked on load.
/// Regenerate the file with tools/Tradeground.ChartData.
/// </summary>
public sealed class ChartLibrary
{
    public const string ResourceName = "Tradeground.Game.Charts.crypto-moments.json";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new CandleArrayConverter() },
    };

    public ChartLibrary(MomentDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (dataset.Moments.Count == 0)
        {
            throw new InvalidOperationException("The chart library has no moments.");
        }

        foreach (var moment in dataset.Moments)
        {
            Validate(moment);
        }

        Source = dataset.Source;
        Moments = dataset.Moments;
    }

    public string Source { get; }

    public IReadOnlyList<ChartMoment> Moments { get; }

    public static IReadOnlyList<Timeframe> Timeframes => Timeframe.Intraday;

    public static ChartLibrary LoadBuiltIn()
    {
        using var stream = typeof(ChartLibrary).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing resource {ResourceName}.");
        var dataset = JsonSerializer.Deserialize<MomentDataset>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Resource {ResourceName} is empty.");
        return new ChartLibrary(dataset);
    }

    /// <summary>
    /// Bad data should stop the app at startup, not surprise a player mid-game. Every frame must be
    /// complete, evenly spaced, valid, and line up on the same "now" with the same price.
    /// </summary>
    public static void Validate(ChartMoment moment)
    {
        ArgumentNullException.ThrowIfNull(moment);
        var where = $"{moment.Symbol} at {moment.Now:u}";

        if (moment.Decimals is < 0 or > 8)
        {
            throw new InvalidOperationException($"{where}: decimals must be between 0 and 8.");
        }

        foreach (var timeframe in Timeframe.Intraday)
        {
            if (!moment.Frames.TryGetValue(timeframe.Id, out var candles))
            {
                throw new InvalidOperationException($"{where}: missing the {timeframe.Id} frame.");
            }

            if (candles.Count != ChartMoment.CandlesPerFrame)
            {
                throw new InvalidOperationException($"{where} {timeframe.Id}: needs exactly {ChartMoment.CandlesPerFrame} candles.");
            }

            for (var i = 0; i < candles.Count; i++)
            {
                if (!candles[i].IsValid)
                {
                    throw new InvalidOperationException($"{where} {timeframe.Id}: candle {i} has impossible prices.");
                }

                // The history ends exactly at "now"; every candle is one timeframe after the previous one.
                var expected = moment.Now + (timeframe.Length * (i - ChartMoment.HistoryCandles));
                if (candles[i].OpenTime != expected)
                {
                    throw new InvalidOperationException($"{where} {timeframe.Id}: candle {i} opens at {candles[i].OpenTime:u}, expected {expected:u}.");
                }
            }

            if (candles[ChartMoment.HistoryCandles - 1].Close != moment.PriceNow)
            {
                throw new InvalidOperationException($"{where} {timeframe.Id}: the price at \"now\" differs from the 1m chart.");
            }
        }
    }
}

public sealed record MomentDataset(string Source, IReadOnlyList<ChartMoment> Moments);

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
