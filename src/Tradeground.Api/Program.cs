using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Tradeground.Api.Charts;
using Tradeground.Api.Endpoints;
using Tradeground.Api.Hubs;
using Tradeground.Api.Rooms;
using Tradeground.Contracts;
using Tradeground.Game;
using Tradeground.Game.Charts;

var builder = WebApplication.CreateBuilder(args);

// Enums travel as "Buy"/"Sell" instead of 0/1: readable, and safe if the enum is reordered.
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services
    .AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => ModeCatalog.LoadBuiltIn());
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddSingleton(_ => ChartLibrary.LoadBuiltIn());
builder.Services.AddSingleton<ChartRunStore>();
builder.Services.AddSingleton<ChartHighlights>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Turns a full room mailbox into 503 + Retry-After instead of a 500.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (RoomBusyException)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "1";
    }
});

// Security headers on every response. The site loads nothing from other origins (fonts are served
// from here), so the policy is strict: only this origin, ever. That blocks injected scripts, and no
// third party learns a player's IP address. 'wasm-unsafe-eval' is the one extra permission Blazor
// needs: it lets the browser compile the .NET runtime, which is WebAssembly, not script.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; img-src 'self' data:; " +
        "base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    await next(context);
});

// The browser app (Tradeground.Web, Blazor WebAssembly) is served from here: one origin, one
// container. App files revalidate on every load so a deploy is never half-old; the fonts never
// change, so they cache for a week. Blazor's own loader also checks every runtime file by hash.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl =
            context.Context.Request.Path.StartsWithSegments("/fonts") ? "public, max-age=604800" : "no-cache",
});

app.MapHealthChecks("/health");
app.MapHub<MarketHub>(MarketEvents.HubPath);
app.MapRoomEndpoints();
app.MapChartEndpoints();

// Unknown API addresses are a 404, never the web page. Every other unknown address loads the app,
// whose router handles it (/chart, /play/ABC123 ...), so deep links and refreshes work.
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Exposed so integration tests can boot the real app with WebApplicationFactory.</summary>
public partial class Program
{
}
