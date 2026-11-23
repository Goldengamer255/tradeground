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
    public async Task Lists_the_timeframes_with_their_market_type()
    {
        using var client = factory.CreateClient();

        var timeframes = await client.GetFromJsonAsync<TimeframeView[]>("/api/charts/timeframes", Json);

        timeframes.ShouldNotBeNull().Select(t => (t.Id, t.Market)).ShouldBe(new[]
        {
            ("1m", "Crypto"), ("15m", "Crypto"), ("30m", "Crypto"), ("1h", "Crypto"), ("4h", "Crypto"), ("1w", "Forex"),
        });
    }

    [Theory]
    [InlineData("1m")]
    [InlineData("4h")]
    [InlineData("1w")]
    public async Task A_new_chart_shows_only_the_history_as_an_index(string timeframe)
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/charts", new NewChartRequest(timeframe), Json);
        var raw = await response.Content.ReadAsStringAsync();
        var started = JsonSerializer.Deserialize<ChartStarted>(raw, Json).ShouldNotBeNull();

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        started.Chart.Timeframe.ShouldBe(timeframe);
        started.Chart.Candles.Count.ShouldBe(ChartRound.HistoryCandles);
        started.Chart.Candles[0].Close.ShouldBe(ChartRound.IndexBase);
        (started.Chart.Result, started.Chart.Reveal).ShouldBe((null, null));

        // The answer must not be anywhere in the response: no market, no dates, no real prices.
        raw.ShouldNotContain("USD");
        Regex.IsMatch(raw, @"\d{4}-\d{2}-\d{2}").ShouldBeFalse("no dates before the call");
    }

    [Fact]
    public async Task Calling_reveals_the_next_stretch_and_the_market()
    {
        using var client = factory.CreateClient();
        var started = await StartAsync(client, "15m");

        var chart = await CallAsync(client, started.Key, Direction.Up);

        chart.Candles.Count.ShouldBe(ChartRound.TotalCandles);
        var result = chart.Result.ShouldNotBeNull();
        result.Call.ShouldBe(Direction.Up);
        result.Correct.ShouldBe(result.Actual is null ? null : result.Actual == Direction.Up);
        var reveal = chart.Reveal.ShouldNotBeNull();
        reveal.Symbol.ShouldEndWith("/USDT");
        (reveal.EndTime - reveal.CallTime).ShouldBe(TimeSpan.FromMinutes(15 * ChartRound.Horizon));
        (chart.Stats.Right + chart.Stats.Wrong + chart.Stats.Flat).ShouldBe(1);
    }

    [Fact]
    public async Task A_chart_can_be_called_only_once_and_the_next_one_starts_fresh()
    {
        using var client = factory.CreateClient();
        var started = await StartAsync(client, "1h");
        await CallAsync(client, started.Key, Direction.Down);

        using var again = await SendAsync(client, "/api/charts/mine/call", started.Key, new CallRequest(Direction.Up));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var next = await SendAsync(client, "/api/charts/mine/next", started.Key, new NewChartRequest("1w"));
        var chart = (await next.Content.ReadFromJsonAsync<ChartView>(Json)).ShouldNotBeNull();
        chart.Timeframe.ShouldBe("1w");
        chart.Candles.Count.ShouldBe(ChartRound.HistoryCandles);
        chart.Result.ShouldBeNull();
        (chart.Stats.Right + chart.Stats.Wrong + chart.Stats.Flat).ShouldBe(1, "the run's tally carries over");
    }

    [Fact]
    public async Task A_refresh_picks_up_where_the_player_left_off()
    {
        using var client = factory.CreateClient();
        var started = await StartAsync(client, "30m");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/charts/mine", UriKind.Relative));
        request.Headers.Add(ChartEndpoints.KeyHeader, started.Key);
        using var response = await client.SendAsync(request);
        var chart = (await response.Content.ReadFromJsonAsync<ChartView>(Json)).ShouldNotBeNull();

        chart.Candles.ShouldBe(started.Chart.Candles);
    }

    [Fact]
    public async Task Bad_requests_are_rejected_clearly()
    {
        using var client = factory.CreateClient();
        var started = await StartAsync(client, "4h");

        using var badTimeframe = await client.PostAsJsonAsync("/api/charts", new NewChartRequest("2h"), Json);
        using var noKey = await SendAsync(client, "/api/charts/mine/call", "not-a-key", new CallRequest(Direction.Up));
        using var badDirection = await SendAsync(client, "/api/charts/mine/call", started.Key, new { direction = 7 });

        badTimeframe.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        noKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        badDirection.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<ChartStarted> StartAsync(HttpClient client, string timeframe)
    {
        using var response = await client.PostAsJsonAsync("/api/charts", new NewChartRequest(timeframe), Json);
        return (await response.Content.ReadFromJsonAsync<ChartStarted>(Json)).ShouldNotBeNull();
    }

    private static async Task<ChartView> CallAsync(HttpClient client, string key, Direction direction)
    {
        using var response = await SendAsync(client, "/api/charts/mine/call", key, new CallRequest(direction));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ChartView>(Json)).ShouldNotBeNull();
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
