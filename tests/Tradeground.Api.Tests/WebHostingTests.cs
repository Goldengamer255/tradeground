using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tradeground.Api.Charts;
using Tradeground.Game.Charts;

namespace Tradeground.Api.Tests;

/// <summary>The API also serves the Blazor app: one origin, one container. These pin down how.</summary>
public sealed class WebHostingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task The_home_page_is_the_blazor_app_behind_a_strict_security_policy()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("_framework/blazor.webassembly.js");
        var csp = response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem();
        csp.ShouldContain("default-src 'self'");
        csp.ShouldContain("script-src 'self' 'wasm-unsafe-eval'", customMessage: "WebAssembly is the one extra permission Blazor needs");
        csp.ShouldNotContain("unsafe-inline");
    }

    [Theory]
    [InlineData("/chart")]
    [InlineData("/play/ABC123")]
    [InlineData("/host/ABC123")]
    public async Task Deep_links_load_the_app_so_refreshing_any_page_works(string path)
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri(path, UriKind.Relative));

        html.ShouldContain("_framework/blazor.webassembly.js");
    }

    [Fact]
    public async Task Unknown_api_addresses_are_a_404_not_the_web_page()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/no-such-thing", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/html");
    }

    [Fact]
    public async Task The_blazor_runtime_is_served()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/_framework/blazor.webassembly.js", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The home page's ticker must never hint at an Up or Down answer: no highlighted candle may overlap
    /// the future part of ANY chart of that market, on any timeframe.
    /// </summary>
    [Fact]
    public void Ticker_highlights_never_overlap_any_charts_future()
    {
        var library = factory.Services.GetRequiredService<ChartLibrary>();
        var highlights = factory.Services.GetRequiredService<ChartHighlights>().All;

        highlights.ShouldNotBeEmpty();
        foreach (var h in highlights)
        {
            var length = Timeframe.Find(h.Timeframe).ShouldNotBeNull().Length;
            foreach (var moment in library.Moments.Where(m => m.Symbol == h.Symbol))
            {
                var futureEnds = moment.Now + (Timeframe.H4.Length * ChartMoment.FutureCandles);
                var overlaps = h.When < futureEnds && h.When + length > moment.Now;
                overlaps.ShouldBeFalse($"{h.Symbol} {h.Timeframe} at {h.When:u} overlaps the future of the chart at {moment.Now:u}");
            }
        }
    }
}
