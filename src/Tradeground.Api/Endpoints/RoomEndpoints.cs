using Microsoft.AspNetCore.SignalR;
using Tradeground.Api.Hubs;
using Tradeground.Api.Rooms;
using Tradeground.Engine;
using Tradeground.Game;

namespace Tradeground.Api.Endpoints;

/// <summary>
/// REST endpoints. Players authenticate with the X-Trader-Key header they got when joining;
/// the room's host uses X-Host-Key. Both are random 128-bit secrets (guest sessions, no accounts).
/// </summary>
public static class RoomEndpoints
{
    public const string TraderKeyHeader = "X-Trader-Key";
    public const string HostKeyHeader = "X-Host-Key";

    public static IEndpointRouteBuilder MapRoomEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/modes", (ModeCatalog modes) => modes.All.Select(ModeSummary.From));

        var rooms = api.MapGroup("/rooms");
        rooms.MapPost("/", CreateRoom);
        rooms.MapPost("/{code}/players", JoinRoom);
        rooms.MapGet("/{code}/book", GetBook);
        rooms.MapGet("/{code}/me", GetAccount);
        rooms.MapPost("/{code}/orders", PlaceOrder);
        rooms.MapDelete("/{code}/orders/{orderId:long}", CancelOrder);
        rooms.MapPost("/{code}/clues", RevealClue);
        rooms.MapPost("/{code}/settlement", Settle);

        return app;
    }

    private static IResult CreateRoom(CreateRoomRequest request, ModeCatalog modes, RoomRegistry registry)
    {
        if (!modes.TryGet(request.ModeId, out var mode))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.ModeId)] = [$"Unknown mode '{request.ModeId}'."],
            });
        }

        var seed = request.Seed ?? (ulong)Random.Shared.NextInt64();
        var room = registry.Create(mode, seed);
        return Results.Created($"/api/rooms/{room.Code}", new CreateRoomResponse(room.Code, room.HostKey, mode.Id));
    }

    private static async Task<IResult> JoinRoom(string code, RoomRegistry registry)
    {
        if (!registry.TryGet(code, out var room))
        {
            return Results.NotFound();
        }

        var traderKey = Secrets.NewToken();
        var displayName = NameGenerator.Next();
        var player = await room.Ask(r => r.Phase == RoomPhase.Trading ? r.Join(traderKey, displayName) : null);
        if (player is null)
        {
            return Results.Conflict("The round is over.");
        }

        return Results.Ok(new JoinRoomResponse(
            traderKey,
            player.DisplayName,
            player.PrivateClue.Text,
            room.Mode.StartingCash,
            room.Mode.MaxPosition));
    }

    private static async Task<IResult> GetBook(string code, RoomRegistry registry)
    {
        if (!registry.TryGet(code, out var room))
        {
            return Results.NotFound();
        }

        return Results.Ok(await room.Ask(r => r.Snapshot()));
    }

    private static async Task<IResult> GetAccount(string code, HttpRequest http, RoomRegistry registry)
    {
        if (!registry.TryGet(code, out var room))
        {
            return Results.NotFound();
        }

        var traderKey = http.Headers[TraderKeyHeader].ToString();
        var account = await room.Ask(r => r.IsPlayer(traderKey) ? r.Ledger.Get(traderKey) : null);
        return account is null
            ? Results.Unauthorized()
            : Results.Ok(new AccountView(account.Cash, account.Position));
    }

    private static async Task<IResult> PlaceOrder(
        string code,
        PlaceOrderRequest request,
        HttpRequest http,
        RoomRegistry registry,
        IHubContext<MarketHub> hub)
    {
        if (!registry.TryGet(code, out var room))
        {
            return Results.NotFound();
        }

        var traderKey = http.Headers[TraderKeyHeader].ToString();
        var order = new OrderRequest(traderKey, request.Side, request.Type, request.Quantity, request.LimitPrice);

        // Validation, matching and the snapshot all happen in ONE work item, so the snapshot
        // reflects exactly the state right after this order. Nothing can sneak in between.
        var outcome = await room.Ask(r => r.IsPlayer(traderKey)
            ? (Result: r.Submit(order), Snapshot: r.Snapshot())
            : default((OrderResult Result, BookSnapshot Snapshot)?));

        if (outcome is not { } o)
        {
            return Results.Unauthorized();
        }

        if (o.Result.Status != OrderStatus.Rejected)
        {
            var group = hub.Clients.Group(MarketHub.GroupFor(room.Code));
            await group.SendAsync("bookUpdated", o.Snapshot);
            if (o.Result.Trades.Count > 0)
            {
                var tape = o.Result.Trades.Select(t => new PublicTrade(t.Sequence, t.Price, t.Quantity, t.AggressorSide)).ToList();
                await group.SendAsync("tradesExecuted", tape);
            }
        }

        // A rejected order is a normal business outcome (like an exchange reject message), so it is
        // 200 OK with Status = Rejected. 400 is reserved for requests that are malformed.
        return Results.Ok(new PlaceOrderResponse(
            o.Result.OrderId,
            o.Result.Status,
            o.Result.FilledQuantity,
            o.Result.RestingQuantity,
            o.Result.Trades.Select(t => new Fill(t.Price, t.Quantity)).ToList(),
            o.Result.RejectReason));
    }

    private static async Task<IResult> CancelOrder(
        string code,
        long orderId,
        HttpRequest http,
        RoomRegistry registry,
        IHubContext<MarketHub> hub)
    {
        if (!registry.TryGet(code, out var room))
        {
            return Results.NotFound();
        }

        var traderKey = http.Headers[TraderKeyHeader].ToString();
        var outcome = await room.Ask(r => r.IsPlayer(traderKey)
            ? (Cancelled: r.Cancel(traderKey, orderId), Snapshot: r.Snapshot())
            : default((bool Cancelled, BookSnapshot Snapshot)?));

        if (outcome is not { } o)
        {
            return Results.Unauthorized();
        }

        if (!o.Cancelled)
        {
            return Results.NotFound();
        }

        await hub.Clients.Group(MarketHub.GroupFor(room.Code)).SendAsync("bookUpdated", o.Snapshot);
        return Results.NoContent();
    }

    private static async Task<IResult> RevealClue(string code, HttpRequest http, RoomRegistry registry, IHubContext<MarketHub> hub)
    {
        if (!registry.TryGet(code, out var room))
        {
            return Results.NotFound();
        }

        if (!Secrets.Matches(http.Headers[HostKeyHeader].ToString(), room.HostKey))
        {
            return Results.Unauthorized();
        }

        var clue = await room.Ask(r => r.RevealPublicClue());
        if (clue is null)
        {
            return Results.Conflict("No more clues to reveal.");
        }

        await hub.Clients.Group(MarketHub.GroupFor(room.Code)).SendAsync("clueRevealed", clue.Text);
        return Results.Ok(clue.Text);
    }

    private static async Task<IResult> Settle(string code, HttpRequest http, RoomRegistry registry, IHubContext<MarketHub> hub)
    {
        if (!registry.TryGet(code, out var room))
        {
            return Results.NotFound();
        }

        if (!Secrets.Matches(http.Headers[HostKeyHeader].ToString(), room.HostKey))
        {
            return Results.Unauthorized();
        }

        var settlement = await room.Ask(r => r.Settle());
        await hub.Clients.Group(MarketHub.GroupFor(room.Code)).SendAsync("roundSettled", settlement);
        return Results.Ok(settlement);
    }
}
