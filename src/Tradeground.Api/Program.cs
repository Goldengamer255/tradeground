using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Tradeground.Api.Charts;
using Tradeground.Api.Endpoints;
using Tradeground.Api.Hubs;
using Tradeground.Api.Rooms;
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
builder.Services.AddSingleton<ChartSessionStore>();
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

// Security headers on every response. The page loads nothing from other sites (fonts and the
// SignalR client are served from here), so the policy can be strict: only this origin, ever.
// That blocks injected scripts, and it means no third party learns a child's IP address.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    await next(context);
});

// The web app is plain files in wwwroot: "/" serves index.html. One container ships both.
// Caching: the app's own files must never be stale after a deploy (old JavaScript talking to a
// new API is a classic bug), so the browser revalidates them every time; unchanged files cost a
// tiny 304. Vendored libraries in /lib change only when we upgrade them, so they cache for a week.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl =
            context.Context.Request.Path.StartsWithSegments("/lib") ? "public, max-age=604800" : "no-cache",
});

app.MapHealthChecks("/health");
app.MapHub<MarketHub>("/hubs/market");
app.MapRoomEndpoints();
app.MapChartEndpoints();

app.Run();

/// <summary>Exposed so integration tests can boot the real app with WebApplicationFactory.</summary>
public partial class Program
{
}
