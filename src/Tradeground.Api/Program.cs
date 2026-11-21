using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Tradeground.Api.Endpoints;
using Tradeground.Api.Hubs;
using Tradeground.Api.Rooms;
using Tradeground.Game;

var builder = WebApplication.CreateBuilder(args);

// Enums travel as "Buy"/"Sell" instead of 0/1: readable, and safe if the enum is reordered.
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services
    .AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => ModeCatalog.LoadBuiltIn());
builder.Services.AddSingleton<RoomRegistry>();
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

app.MapHealthChecks("/health");
app.MapHub<MarketHub>("/hubs/market");
app.MapRoomEndpoints();

app.Run();

/// <summary>Exposed so integration tests can boot the real app with WebApplicationFactory.</summary>
public partial class Program
{
}
