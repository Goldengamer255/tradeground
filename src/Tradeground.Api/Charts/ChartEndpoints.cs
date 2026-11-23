using System.Globalization;
using Tradeground.Game.Charts;

namespace Tradeground.Api.Charts;

/// <summary>
/// Chart Replay: a solo game on real historical charts. Look at the chart, call Up or Down once,
/// then the next stretch is revealed. POST /api/charts starts a run and returns a secret key;
/// send it as X-Chart-Key afterwards.
/// </summary>
public static class ChartEndpoints
{
    public const string KeyHeader = "X-Chart-Key";

    public static IEndpointRouteBuilder MapChartEndpoints(this IEndpointRouteBuilder app)
    {
        var charts = app.MapGroup("/api/charts");
        charts.MapGet("/timeframes", (ChartLibrary library) => library.Timeframes.Select(TimeframeView.From));
        charts.MapPost("/", Start);
        charts.MapGet("/mine", Get);
        charts.MapPost("/mine/next", Next);
        charts.MapPost("/mine/call", Call);
        return app;
    }

    private static IResult Start(NewChartRequest request, ChartRunStore store)
    {
        if (!TryTimeframe(store.Library, request.Timeframe, out var timeframe, out var invalid))
        {
            return invalid;
        }

        if (store.Start((ulong)Random.Shared.NextInt64()) is not { } started)
        {
            return Results.Problem("Too many games are running right now. Try again in a few minutes.", statusCode: 503);
        }

        var view = store.Use(started.Key, run =>
        {
            run.Next(timeframe);
            return ChartView.From(run);
        });
        return Results.Created("/api/charts/mine", new ChartStarted(started.Key, view!));
    }

    private static IResult Get(HttpRequest http, ChartRunStore store)
    {
        var view = store.Use(http.Headers[KeyHeader].ToString(), ChartView.From);
        return view is null ? Results.Unauthorized() : Results.Ok(view);
    }

    private static IResult Next(NewChartRequest request, HttpRequest http, ChartRunStore store)
    {
        if (!TryTimeframe(store.Library, request.Timeframe, out var timeframe, out var invalid))
        {
            return invalid;
        }

        var view = store.Use(http.Headers[KeyHeader].ToString(), run =>
        {
            run.Next(timeframe);
            return ChartView.From(run);
        });
        return view is null ? Results.Unauthorized() : Results.Ok(view);
    }

    private static IResult Call(CallRequest request, HttpRequest http, ChartRunStore store)
    {
        if (!Enum.IsDefined(request.Direction))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Direction)] = ["Call Up or Down."],
            });
        }

        // Check and call under one lock, so a double-click can't call the same chart twice.
        var outcome = store.Use(http.Headers[KeyHeader].ToString(), run =>
        {
            if (run.Current is not { IsCalled: false })
            {
                return new CallOutcome(null);
            }

            run.Call(request.Direction);
            return new CallOutcome(ChartView.From(run));
        });

        return outcome switch
        {
            null => Results.Unauthorized(),
            { View: null } => Results.Conflict("You already called this chart. Deal a new one."),
            { View: var view } => Results.Ok(view),
        };
    }

    private static bool TryTimeframe(ChartLibrary library, string? id, out Timeframe timeframe, out IResult invalid)
    {
        timeframe = library.Timeframes.FirstOrDefault(t => t.Id == id)!;
        invalid = Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["Timeframe"] = [$"Choose one of: {string.Join(", ", library.Timeframes.Select(t => t.Id))}."],
        });
        return timeframe is not null;
    }

    /// <summary>View is null when the chart was already called.</summary>
    private sealed record CallOutcome(ChartView? View);
}

public sealed record NewChartRequest(string? Timeframe);

public sealed record CallRequest(Direction Direction);

public sealed record ChartStarted(string Key, ChartView Chart);

/// <summary>A timeframe the page can offer, and what kind of market it shows.</summary>
public sealed record TimeframeView(string Id, string Label, string Market)
{
    public static TimeframeView From(Timeframe timeframe) =>
        new(timeframe.Id, timeframe.Label, timeframe == Timeframe.W1 ? "Forex" : "Crypto");
}

/// <summary>A candle as players see it: rebased to an index, with no time.</summary>
public sealed record CandleView(long Open, long High, long Low, long Close)
{
    public static CandleView From(ChartRound round, Candle candle)
    {
        var c = round.Rebase(candle);
        return new CandleView(c.Open, c.High, c.Low, c.Close);
    }
}

public sealed record RunStats(int Right, int Wrong, int Flat, int Streak, int BestStreak)
{
    public static RunStats From(ChartRun run) => new(run.Right, run.Wrong, run.Flat, run.Streak, run.BestStreak);
}

public sealed record CallView(Direction Call, Direction? Actual, bool? Correct, decimal ChangePercent)
{
    public static CallView From(CallResult result) => new(result.Call, result.Actual, result.Correct, result.ChangePercent);
}

/// <summary>
/// Everything the page needs. Before the call it holds only the history, as an index with no times;
/// <see cref="Result"/> and <see cref="Reveal"/> stay null, so the market, the dates and the future
/// can't be read from the network traffic.
/// </summary>
public sealed record ChartView(
    string Timeframe,
    int HistoryCandles,
    int Horizon,
    IReadOnlyList<CandleView> Candles,
    CallView? Result,
    ChartReveal? Reveal,
    RunStats Stats)
{
    public static ChartView From(ChartRun run)
    {
        var round = run.Current ?? throw new InvalidOperationException("Deal a chart first.");
        return new ChartView(
            round.Timeframe.Id,
            ChartRound.HistoryCandles,
            ChartRound.Horizon,
            round.Visible.Select(c => CandleView.From(round, c)).ToList(),
            round.Result is { } result ? CallView.From(result) : null,
            round.IsCalled ? ChartReveal.From(round) : null,
            RunStats.From(run));
    }
}

/// <summary>The answer: which market and when, with the real prices at the call and at the end.</summary>
public sealed record ChartReveal(
    string Symbol,
    string Name,
    DateTimeOffset CallTime,
    DateTimeOffset EndTime,
    string CallPrice,
    string EndPrice)
{
    public static ChartReveal From(ChartRound round)
    {
        var candles = round.Visible;
        var result = round.Result ?? throw new InvalidOperationException("Not called yet.");
        var decimals = round.Series.Decimals;
        return new ChartReveal(
            round.Series.Symbol,
            round.Series.Name,
            candles[ChartRound.HistoryCandles - 1].OpenTime + round.Timeframe.Length,
            candles[^1].OpenTime + round.Timeframe.Length,
            Format(result.DecisionClose, decimals),
            Format(result.EndClose, decimals));
    }

    /// <summary>Integer ticks back to a quote: 10850 with 4 decimals is "1.0850".</summary>
    public static string Format(long ticks, int decimals) =>
        (ticks / (decimal)Math.Pow(10, decimals)).ToString($"N{decimals}", CultureInfo.InvariantCulture);
}
