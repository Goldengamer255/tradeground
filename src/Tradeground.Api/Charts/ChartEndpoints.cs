using System.Globalization;
using Tradeground.Game.Charts;

namespace Tradeground.Api.Charts;

/// <summary>
/// Chart Replay: a solo game on real historical exchange rates.
/// POST /api/charts starts a game and returns a secret key; send it as X-Chart-Key afterwards.
/// </summary>
public static class ChartEndpoints
{
    public const string KeyHeader = "X-Chart-Key";

    public static IEndpointRouteBuilder MapChartEndpoints(this IEndpointRouteBuilder app)
    {
        var charts = app.MapGroup("/api/charts");
        charts.MapPost("/", Start);
        charts.MapGet("/mine", Get);
        charts.MapPost("/mine/guesses", Guess);
        return app;
    }

    private static IResult Start(ChartSessionStore store)
    {
        if (store.Start((ulong)Random.Shared.NextInt64()) is not { } started)
        {
            return Results.Problem("Too many games are running right now. Try again in a few minutes.", statusCode: 503);
        }

        return Results.Created("/api/charts/mine", new ChartStarted(started.Key, ChartView.From(started.Session)));
    }

    private static IResult Get(HttpRequest http, ChartSessionStore store)
    {
        var view = store.Use(http.Headers[KeyHeader].ToString(), ChartView.From);
        return view is null ? Results.Unauthorized() : Results.Ok(view);
    }

    private static IResult Guess(GuessRequest request, HttpRequest http, ChartSessionStore store)
    {
        if (!Enum.IsDefined(request.Prediction))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Prediction)] = ["Choose StrongBear, Bear, Unsure, Bull or StrongBull."],
            });
        }

        // Check and guess under one lock, so a double-click can't play a round twice.
        var outcome = store.Use(http.Headers[KeyHeader].ToString(), session =>
        {
            if (session.IsFinished)
            {
                return new GuessOutcome(null);
            }

            var result = session.Guess(request.Prediction);
            return new GuessOutcome(new GuessResponse(GuessView.From(session, result), ChartView.From(session)));
        });

        return outcome switch
        {
            null => Results.Unauthorized(),
            { Response: null } => Results.Conflict("This chart is finished. Start a new one."),
            { Response: var response } => Results.Ok(response),
        };
    }

    /// <summary>Response is null when the game was already over.</summary>
    private sealed record GuessOutcome(GuessResponse? Response);
}

public sealed record GuessRequest(Prediction Prediction);

public sealed record ChartStarted(string Key, ChartView Chart);

public sealed record GuessResponse(GuessView Guess, ChartView Chart);

/// <summary>A candle as players see it: rebased to an index, with no date.</summary>
public sealed record CandleView(long Open, long High, long Low, long Close)
{
    public static CandleView From(ChartSession session, Candle candle)
    {
        var c = session.Rebase(candle);
        return new CandleView(c.Open, c.High, c.Low, c.Close);
    }
}

public sealed record GuessView(int Round, Prediction Prediction, Move Move, int Points, decimal ChangePercent)
{
    public static GuessView From(ChartSession session, GuessResult result)
    {
        var index = session.Start + ChartSession.HistoryCandles + result.Round - 1;
        var before = session.Series.Candles[index - 1].Close;
        var change = Math.Round((result.Revealed.Close - before) * 100m / before, 2);
        return new GuessView(result.Round, result.Prediction, result.Move, result.Points, change);
    }
}

/// <summary>
/// Everything the page needs. <see cref="Reveal"/> stays null until the last round, so the pair,
/// the dates and the real prices can't be read from the network traffic mid-game.
/// </summary>
public sealed record ChartView(
    int Rounds,
    int RoundsPlayed,
    int Score,
    int HistoryCandles,
    IReadOnlyList<CandleView> Candles,
    IReadOnlyList<GuessView> Guesses,
    ChartReveal? Reveal)
{
    public static ChartView From(ChartSession session) =>
        new(
            ChartSession.Rounds,
            session.RoundsPlayed,
            session.Score,
            ChartSession.HistoryCandles,
            session.Visible.Select(c => CandleView.From(session, c)).ToList(),
            session.Guesses.Select(g => GuessView.From(session, g)).ToList(),
            session.IsFinished ? ChartReveal.From(session) : null);
}

/// <summary>The answer: which market and when, with the real prices.</summary>
public sealed record ChartReveal(
    string Symbol,
    string Name,
    DateOnly FirstWeek,
    DateOnly LastWeek,
    string FirstClose,
    string LastClose,
    int Right,
    int Wrong,
    int NotSure,
    int Flat,
    int BestPossible)
{
    public static ChartReveal From(ChartSession session)
    {
        var all = session.All;
        var guesses = session.Guesses;
        return new ChartReveal(
            session.Series.Symbol,
            session.Series.Name,
            all[0].WeekStart,
            all[^1].WeekStart,
            Format(all[0].Close, session.Series.Decimals),
            Format(all[^1].Close, session.Series.Decimals),
            // Every round lands in exactly one bucket: right, wrong, "not sure", or a flat week.
            guesses.Count(g => g.Points > 0),
            guesses.Count(g => g.Points < 0),
            guesses.Count(g => g.Prediction == Prediction.Unsure),
            guesses.Count(g => g.Move == Move.Flat && g.Prediction != Prediction.Unsure),
            guesses.Count(g => g.Move != Move.Flat) * Scoring.Points(Prediction.StrongBull, Move.Up));
    }

    /// <summary>Integer ticks back to a quote: 10850 with 4 decimals is "1.0850".</summary>
    public static string Format(long ticks, int decimals) =>
        (ticks / (decimal)Math.Pow(10, decimals)).ToString($"F{decimals}", CultureInfo.InvariantCulture);
}
