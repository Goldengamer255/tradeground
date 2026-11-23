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
    public async Task A_new_chart_shows_history_as_an_index_and_nothing_else()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(new Uri("/api/charts", UriKind.Relative), content: null);
        var raw = await response.Content.ReadAsStringAsync();
        var started = JsonSerializer.Deserialize<ChartStarted>(raw, Json).ShouldNotBeNull();

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        started.Chart.Candles.Count.ShouldBe(ChartSession.HistoryCandles);
        started.Chart.Candles[0].Close.ShouldBe(ChartSession.IndexBase);
        started.Chart.Reveal.ShouldBeNull();

        // The answer must not be anywhere in the response: no pair, no dates, no real prices.
        raw.ShouldNotContain("USD");
        raw.ShouldNotContain("week", Case.Insensitive);
        Regex.IsMatch(raw, @"\d{4}-\d{2}-\d{2}").ShouldBeFalse("no dates until the end");
    }

    [Fact]
    public async Task Twenty_guesses_finish_the_game_and_reveal_the_market()
    {
        using var client = factory.CreateClient();
        var started = await StartAsync(client);

        GuessResponse? last = null;
        for (var i = 0; i < ChartSession.Rounds; i++)
        {
            last = await GuessAsync(client, started.Key, (Prediction)(i % 5));
        }

        var chart = last.ShouldNotBeNull().Chart;
        chart.RoundsPlayed.ShouldBe(ChartSession.Rounds);
        chart.Candles.Count.ShouldBe(ChartSession.TotalCandles);
        chart.Score.ShouldBe(chart.Guesses.Sum(g => g.Points));
        var reveal = chart.Reveal.ShouldNotBeNull();
        reveal.Symbol.Length.ShouldBe(6);
        (reveal.Right + reveal.Wrong + reveal.NotSure + reveal.Flat).ShouldBe(ChartSession.Rounds, "every round is counted exactly once");
        reveal.NotSure.ShouldBe(4, "every fifth call was Not sure");
        reveal.LastWeek.ShouldBeGreaterThan(reveal.FirstWeek);

        using var extra = await SendGuessAsync(client, started.Key, Prediction.Bull);
        extra.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_refresh_picks_up_where_the_player_left_off()
    {
        using var client = factory.CreateClient();
        var started = await StartAsync(client);
        await GuessAsync(client, started.Key, Prediction.Unsure);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/charts/mine", UriKind.Relative));
        request.Headers.Add(ChartEndpoints.KeyHeader, started.Key);
        using var response = await client.SendAsync(request);
        var chart = (await response.Content.ReadFromJsonAsync<ChartView>(Json)).ShouldNotBeNull();

        chart.RoundsPlayed.ShouldBe(1);
        chart.Candles.Count.ShouldBe(ChartSession.HistoryCandles + 1);
    }

    [Fact]
    public async Task Guesses_need_a_valid_key_and_a_valid_prediction()
    {
        using var client = factory.CreateClient();
        var started = await StartAsync(client);

        using var noKey = await SendGuessAsync(client, "not-a-key", Prediction.Bull);
        using var badPrediction = await SendRawAsync(client, started.Key, new { prediction = 42 });

        noKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        badPrediction.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<ChartStarted> StartAsync(HttpClient client)
    {
        using var response = await client.PostAsync(new Uri("/api/charts", UriKind.Relative), content: null);
        return (await response.Content.ReadFromJsonAsync<ChartStarted>(Json)).ShouldNotBeNull();
    }

    private static async Task<GuessResponse> GuessAsync(HttpClient client, string key, Prediction prediction)
    {
        using var response = await SendGuessAsync(client, key, prediction);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<GuessResponse>(Json)).ShouldNotBeNull();
    }

    private static Task<HttpResponseMessage> SendGuessAsync(HttpClient client, string key, Prediction prediction) =>
        SendRawAsync(client, key, new GuessRequest(prediction));

    private static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/charts/mine/guesses", UriKind.Relative))
        {
            Content = JsonContent.Create(body, body.GetType(), options: Json),
        };
        request.Headers.Add(ChartEndpoints.KeyHeader, key);
        return await client.SendAsync(request);
    }
}
