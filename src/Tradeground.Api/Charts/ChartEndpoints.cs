using System.Globalization;
using Tradeground.Contracts;
using Tradeground.Game.Charts;
using Direction = Tradeground.Contracts.Direction;
using GameDirection = Tradeground.Game.Charts.Direction;

namespace Tradeground.Api.Charts;

/// <summary>
/// Up or Down: one real chart frozen at "now", viewable at every timeframe. Call Up or Down on the
/// timeframe you're viewing, then the future is revealed. POST /api/charts starts a run and returns
/// a secret key; send it as X-Chart-Key afterwards.
/// </summary>
public static class ChartEndpoints
{
    public static IEndpointRouteBuilder MapChartEndpoints(this IEndpointRouteBuilder app)
    {
        var charts = app.MapGroup("/api/charts");
        charts.MapGet("/timeframes", () => ChartLibrary.Timeframes.Select(t => new TimeframeView(t.Id, t.Label)));
        charts.MapGet("/highlights", (ChartHighlights highlights) => highlights.All);
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
            return ChartMapping.ToView(run, timeframe);
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

        var view = store.Use(http.Headers[ApiHeaders.ChartKey].ToString(), run => ChartMapping.ToView(run, tf));
        return view is null ? Results.Unauthorized() : Results.Ok(view);
    }

    private static IResult Next(TimeframeRequest request, HttpRequest http, ChartRunStore store)
    {
        if (Timeframe.Find(request.Timeframe) is not { } timeframe)
        {
            return InvalidTimeframe();
        }

        var view = store.Use(http.Headers[ApiHeaders.ChartKey].ToString(), run =>
        {
            run.Next();
            return ChartMapping.ToView(run, timeframe);
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
        var outcome = store.Use(http.Headers[ApiHeaders.ChartKey].ToString(), run =>
        {
            if (run.Current is not { IsCalled: false })
            {
                return new CallOutcome(null);
            }

            run.Call(request.Direction == Direction.Up ? GameDirection.Up : GameDirection.Down, timeframe);
            return new CallOutcome(ChartMapping.ToView(run, timeframe));
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

/// <summary>Turns a run's current chart into what the player is allowed to see.</summary>
public static class ChartMapping
{
    public static ChartView ToView(ChartRun run, Timeframe timeframe)
    {
        var round = run.Current ?? throw new InvalidOperationException("Deal a chart first.");
        return new ChartView(
            timeframe.Id,
            ChartMoment.HistoryCandles,
            ChartMoment.FutureCandles,
            round.Visible(timeframe).Select(c => ToView(round.Rebase(c))).ToList(),
            round.Result is { } result ? ToView(result) : null,
            round.Result is { } called ? ToReveal(round.Moment, called) : null,
            new RunStats(run.Right, run.Wrong, run.Flat, run.Streak, run.BestStreak));
    }

    private static CandleView ToView(Candle c) => new(c.Open, c.High, c.Low, c.Close);

    private static CallView ToView(CallResult result) => new(
        result.Timeframe.Id,
        ToContract(result.Call),
        result.Actual is { } actual ? ToContract(actual) : null,
        result.Correct,
        result.ChangePercent);

    private static Direction ToContract(GameDirection direction) => direction == GameDirection.Up ? Direction.Up : Direction.Down;

    private static ChartReveal ToReveal(ChartMoment moment, CallResult result) => new(
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

/// <summary>
/// Real moves for the home page's ticker tape: the biggest single 1h or 4h candle per market and month.
/// </summary>
/// <remarks>
/// The tape must never hint at an Up or Down answer. A month holds several moments a few hours apart,
/// so a candle that is history for a later moment can be the FUTURE of an earlier one. Taking candles
/// only from before the month's EARLIEST "now" keeps every highlight out of every chart's future.
/// Worked out once at startup; the data never changes while the app runs.
/// </remarks>
public sealed class ChartHighlights(ChartLibrary library)
{
    private const int Count = 16;

    public IReadOnlyList<ChartHighlight> All { get; } = library.Moments
        .GroupBy(m => (m.Symbol, m.Now.Year, m.Now.Month))
        .Select(month => month.MinBy(m => m.Now)!)
        .Select(earliest => new[] { Timeframe.H1, Timeframe.H4 }
            .Select(t =>
            {
                var biggest = earliest.Frame(t).Take(ChartMoment.HistoryCandles).MaxBy(c => Math.Abs(c.Close - c.Open));
                var change = Math.Round((biggest.Close - biggest.Open) * 100m / biggest.Open, 2);
                return new ChartHighlight(earliest.Symbol, biggest.OpenTime, t.Id, change);
            })
            .MaxBy(h => Math.Abs(h.ChangePercent))!)
        .OrderByDescending(h => Math.Abs(h.ChangePercent))
        .Take(Count)
        .OrderBy(h => h.When)
        .ToList();
}
