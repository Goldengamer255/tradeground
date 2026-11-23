using System.Globalization;
using Tradeground.Game.Charts;

namespace Tradeground.Api.Charts;

/// <summary>
/// Up or Down: one real chart frozen at "now", viewable at every timeframe. Call Up or Down on the
/// timeframe you're viewing, then the future is revealed. POST /api/charts starts a run and returns
/// a secret key; send it as X-Chart-Key afterwards.
/// </summary>
public static class ChartEndpoints
{
    public const string KeyHeader = "X-Chart-Key";

    public static IEndpointRouteBuilder MapChartEndpoints(this IEndpointRouteBuilder app)
    {
        var charts = app.MapGroup("/api/charts");
        charts.MapGet("/timeframes", () => ChartLibrary.Timeframes.Select(t => new TimeframeView(t.Id, t.Label)));
        charts.MapPost("/", Start);
        charts.MapGet("/mine", View);
        charts.MapPost("/mine/next", Next);
        charts.MapPost("/mine/call", Call);
        return app;
    }

    private static IResult Start(TimeframeRequest request, ChartRunStore store)
    {
        if (Timeframe.Find(request.Timeframe) is not { } timeframe)
        {
            return InvalidTimeframe();
        }

        if (store.Start((ulong)Random.Shared.NextInt64()) is not { } started)
        {
            return Results.Problem("Too many games are running right now. Try again in a few minutes.", statusCode: 503);
        }

        var view = store.Use(started.Key, run =>
        {
            run.Next();
            return ChartView.From(run, timeframe);
        });
        return Results.Created("/api/charts/mine", new ChartStarted(started.Key, view!));
    }

    /// <summary>The current chart at any timeframe. Looking never changes anything.</summary>
    private static IResult View(string? timeframe, HttpRequest http, ChartRunStore store)
    {
        if (Timeframe.Find(timeframe) is not { } tf)
        {
            return InvalidTimeframe();
        }

        var view = store.Use(http.Headers[KeyHeader].ToString(), run => ChartView.From(run, tf));
        return view is null ? Results.Unauthorized() : Results.Ok(view);
    }

    private static IResult Next(TimeframeRequest request, HttpRequest http, ChartRunStore store)
    {
        if (Timeframe.Find(request.Timeframe) is not { } timeframe)
        {
            return InvalidTimeframe();
        }

        var view = store.Use(http.Headers[KeyHeader].ToString(), run =>
        {
            run.Next();
            return ChartView.From(run, timeframe);
        });
        return view is null ? Results.Unauthorized() : Results.Ok(view);
    }

    private static IResult Call(CallRequest request, HttpRequest http, ChartRunStore store)
    {
        if (Timeframe.Find(request.Timeframe) is not { } timeframe)
        {
            return InvalidTimeframe();
        }

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

            run.Call(request.Direction, timeframe);
            return new CallOutcome(ChartView.From(run, timeframe));
        });

        return outcome switch
        {
            null => Results.Unauthorized(),
            { View: null } => Results.Conflict("You already called this chart. Deal a new one."),
            { View: var view } => Results.Ok(view),
        };
    }

    private static IResult InvalidTimeframe() => Results.ValidationProblem(new Dictionary<string, string[]>
    {
        ["Timeframe"] = [$"Choose one of: {string.Join(", ", ChartLibrary.Timeframes.Select(t => t.Id))}."],
    });

    /// <summary>View is null when the chart was already called.</summary>
    private sealed record CallOutcome(ChartView? View);
}

public sealed record TimeframeRequest(string? Timeframe);

public sealed record CallRequest(Direction Direction, string? Timeframe);

public sealed record ChartStarted(string Key, ChartView Chart);

public sealed record TimeframeView(string Id, string Label);

/// <summary>A candle as players see it: rebased so "now" is 100.00, with no time.</summary>
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

/// <summary>The call, including the timeframe it was made on (which sets how far ahead it looked).</summary>
public sealed record CallView(string Timeframe, Direction Call, Direction? Actual, bool? Correct, decimal ChangePercent)
{
    public static CallView From(CallResult result) =>
        new(result.Timeframe.Id, result.Call, result.Actual, result.Correct, result.ChangePercent);
}

/// <summary>
/// The current chart at one timeframe. Before the call it holds only the history, as an index with
/// no times; <see cref="Result"/> and <see cref="Reveal"/> stay null, so the market, the dates and
/// the future can't be read from the network traffic.
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
    public static ChartView From(ChartRun run, Timeframe timeframe)
    {
        var round = run.Current ?? throw new InvalidOperationException("Deal a chart first.");
        return new ChartView(
            timeframe.Id,
            ChartMoment.HistoryCandles,
            ChartMoment.FutureCandles,
            round.Visible(timeframe).Select(c => CandleView.From(round, c)).ToList(),
            round.Result is { } result ? CallView.From(result) : null,
            round.Result is { } called ? ChartReveal.From(round.Moment, called) : null,
            RunStats.From(run));
    }
}

/// <summary>The answer: which market and when, with the real prices at "now" and when the call was decided.</summary>
public sealed record ChartReveal(
    string Symbol,
    string Name,
    DateTimeOffset Now,
    DateTimeOffset DecidedAt,
    string PriceNow,
    string EndPrice)
{
    public static ChartReveal From(ChartMoment moment, CallResult result) => new(
        moment.Symbol,
        moment.Name,
        moment.Now,
        moment.Now + result.Horizon,
        Format(result.PriceNow, moment.Decimals),
        Format(result.EndPrice, moment.Decimals));

    /// <summary>Integer ticks back to a quote: 6425017 with 2 decimals is "64,250.17".</summary>
    public static string Format(long ticks, int decimals) =>
        (ticks / (decimal)Math.Pow(10, decimals)).ToString($"N{decimals}", CultureInfo.InvariantCulture);
}
