using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Tradeground.Api.Charts;
using Tradeground.Game.Charts;

namespace Tradeground.Api.Tests;

public sealed class ChartApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task Lists_the_timeframes()
    {
        using var client = factory.CreateClient();

        var timeframes = await client.GetFromJsonAsync<TimeframeView[]>("/api/charts/timeframes", Json);

        timeframes.ShouldNotBeNull().Select(t => t.Id).ShouldBe(new[] { "1m", "15m", "30m", "1h", "4h" });
    }

    [Fact]
    public async Task A_new_chart_shows_only_the_history_as_an_index()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/charts", new TimeframeRequest("15m"), Json);
        var raw = await response.Content.ReadAsStringAsync();
        var started = JsonSerializer.Deserialize<ChartStarted>(raw, Json).ShouldNotBeNull();

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        started.Chart.Candles.Count.ShouldBe(ChartMoment.HistoryCandles);
        started.Chart.Candles[^1].Close.ShouldBe(ChartRound.IndexBase, "\"now\" is 100.00");
        (started.Chart.Result, started.Chart.Reveal).ShouldBe((null, null));

        // The answer must not be anywhere in the response: no market, no dates, no real prices.
        raw.ShouldNotContain("USDT");
        Regex.IsMatch(raw, @"\d{4}-\d{2}-\d{2}").ShouldBeFalse("no dates before the call");
    }

    /// <summary>Switching timeframe zooms the same chart: it does not deal a new one.</summary>
    [Fact]
    public async Task Every_timeframe_shows_the_same_chart()
    {
        using var client = factory.CreateClient();
        var started = await StartAsync(client, "15m");

        foreach (var timeframe in new[] { "1m", "15m", "30m", "1h", "4h" })
        {
            var view = await ViewAsync(client, started.Key, timeframe);
            view.Timeframe.ShouldBe(timeframe);
            view.Candles.Count.ShouldBe(ChartMoment.HistoryCandles);
            view.Candles[^1].Close.ShouldBe(ChartRound.IndexBase, $"{timeframe} ends at the same \"now\"");
        }

        (await ViewAsync(client, started.Key, "15m")).Candles.ShouldBe(started.Chart.Candles, "looking around changes nothing");

        var called = await CallAsync(client, started.Key, Direction.Up, "1h");
        var symbol = called.Reveal.ShouldNotBeNull().Symbol;
        foreach (var timeframe in new[] { "1m", "4h" })
        {
            var after = await ViewAsync(client, started.Key, timeframe);
            after.Candles.Count.ShouldBe(ChartMoment.CandlesPerFrame, "after the call every zoom level shows what happened");
            after.Reveal.ShouldNotBeNull().Symbol.ShouldBe(symbol);
            after.Result.ShouldNotBeNull().Timeframe.ShouldBe("1h", "the result stays tied to the timeframe it was called on");
        }
    }

    [Fact]
    public async Task The_call_is_decided_20_candles_ahead_on_its_timeframe()
    {
        using var client = factory.CreateClient();
        var started = await StartAsync(client, "4h");

        var chart = await CallAsync(client, started.Key, Direction.Down, "4h");

        var reveal = chart.Reveal.ShouldNotBeNull();
        (reveal.DecidedAt - reveal.Now).ShouldBe(TimeSpan.FromHours(4 * ChartMoment.FutureCandles));
        var result = chart.Result.ShouldNotBeNull();
        result.Correct.ShouldBe(result.Actual is null ? null : result.Actual == Direction.Down);
        (chart.Stats.Right + chart.Stats.Wrong + chart.Stats.Flat).ShouldBe(1);
    }

    [Fact]
    public async Task A_chart_can_be_called_only_once_and_the_next_one_starts_fresh()
    {
        using var client = factory.CreateClient();
        var started = await StartAsync(client, "1h");
        await CallAsync(client, started.Key, Direction.Down, "1h");

        using var again = await SendAsync(client, "/api/charts/mine/call", started.Key, new CallRequest(Direction.Up, "1h"));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var next = await SendAsync(client, "/api/charts/mine/next", started.Key, new TimeframeRequest("30m"));
        var chart = (await next.Content.ReadFromJsonAsync<ChartView>(Json)).ShouldNotBeNull();
        chart.Candles.Count.ShouldBe(ChartMoment.HistoryCandles);
        chart.Result.ShouldBeNull();
        (chart.Stats.Right + chart.Stats.Wrong + chart.Stats.Flat).ShouldBe(1, "the run's tally carries over");
    }

    [Fact]
    public async Task Bad_requests_are_rejected_clearly()
    {
        using var client = factory.CreateClient();
        var started = await StartAsync(client, "4h");

        using var badTimeframe = await client.PostAsJsonAsync("/api/charts", new TimeframeRequest("2h"), Json);
        using var badView = await client.SendAsync(Get($"/api/charts/mine?timeframe=1w", started.Key));
        using var noKey = await SendAsync(client, "/api/charts/mine/call", "not-a-key", new CallRequest(Direction.Up, "1m"));
        using var badDirection = await SendAsync(client, "/api/charts/mine/call", started.Key, new { direction = 7, timeframe = "1m" });

        badTimeframe.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        badView.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        noKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        badDirection.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<ChartStarted> StartAsync(HttpClient client, string timeframe)
    {
        using var response = await client.PostAsJsonAsync("/api/charts", new TimeframeRequest(timeframe), Json);
        return (await response.Content.ReadFromJsonAsync<ChartStarted>(Json)).ShouldNotBeNull();
    }

    private static async Task<ChartView> ViewAsync(HttpClient client, string key, string timeframe)
    {
        using var response = await client.SendAsync(Get($"/api/charts/mine?timeframe={timeframe}", key));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ChartView>(Json)).ShouldNotBeNull();
    }

    private static async Task<ChartView> CallAsync(HttpClient client, string key, Direction direction, string timeframe)
    {
        using var response = await SendAsync(client, "/api/charts/mine/call", key, new CallRequest(direction, timeframe));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ChartView>(Json)).ShouldNotBeNull();
    }

    private static HttpRequestMessage Get(string url, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Add(ChartEndpoints.KeyHeader, key);
        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string url, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, body.GetType(), options: Json),
        };
        request.Headers.Add(ChartEndpoints.KeyHeader, key);
        return await client.SendAsync(request);
    }
}
