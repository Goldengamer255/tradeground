using Microsoft.AspNetCore.SignalR;
using Tradeground.Api.Hubs;
using Tradeground.Api.Rooms;
using Tradeground.Contracts;
using Tradeground.Engine;
using Tradeground.Game;
using GamePhase = Tradeground.Game.RoomPhase;

namespace Tradeground.Api.Endpoints;

/// <summary>
/// REST endpoints. Players authenticate with the X-Trader-Key header they got when joining;
/// the room's host uses X-Host-Key. Both are random 128-bit secrets (guest sessions, no accounts).
/// </summary>
public static class RoomEndpoints
{
    private const string TraderKeyHeader = ApiHeaders.TraderKey;
    private const string HostKeyHeader = ApiHeaders.HostKey;

    /// <summary>A classroom is ~30; the cap stops one room from growing without limit.</summary>
    public const int MaxPlayersPerRoom = 100;

    /// <summary>How much of the trade tape a late joiner gets.</summary>
    private const int RecentTradeCount = 50;

    public static IEndpointRouteBuilder MapRoomEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/modes", (ModeCatalog modes) => modes.All.Select(m => m.ToSummary()));

        var rooms = api.MapGroup("/rooms");
        rooms.MapPost("/", CreateRoom);
        rooms.MapGet("/{code}", GetRoom);
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

    private static async Task<IResult> GetRoom(string code, RoomRegistry registry)
    {
        if (!registry.TryGet(code, out var room))
        {
            return Results.NotFound();
        }

        return Results.Ok(await room.Ask(r => new RoomView(
            room.Code,
            r.Mode.ToSummary(),
            r.Phase.ToContract(),
            r.Players.Count,
            r.PublicClues.Select(c => c.ToView()).ToList(),
            r.Tape.TakeLast(RecentTradeCount).Select(t => t.ToPublic()).ToList(),
            r.Settlement?.ToView())));
    }

    private static async Task<IResult> JoinRoom(string code, RoomRegistry registry, IHubContext<MarketHub> hub)
    {
        if (!registry.TryGet(code, out var room))
        {
            return Results.NotFound();
        }

        var traderKey = Secrets.NewToken();

        // The name is picked INSIDE the room's worker, so checking "is it taken?" and joining
        // happen as one step. Two players joining at the same moment can't both get the same name.
        var joined = await room.Ask(r =>
        {
            if (r.Phase != GamePhase.Trading)
            {
                return new JoinOutcome(null, "The round is over.", 0);
            }

            if (r.Players.Count >= MaxPlayersPerRoom)
            {
                return new JoinOutcome(null, "The room is full.", 0);
            }

            string name;
            do
            {
                name = NameGenerator.Next();
            }
            while (r.IsNameTaken(name));

            var joinedPlayer = r.Join(traderKey, name);
            return new JoinOutcome(joinedPlayer, null, r.Players.Count);
        });

        if (joined.Player is not { } player)
        {
            return Results.Conflict(joined.Error);
        }

        await hub.Clients.Group(MarketHub.GroupFor(room.Code)).SendAsync(MarketEvents.PlayerJoined, joined.Count);

        return Results.Ok(new JoinRoomResponse(
            traderKey,
            player.DisplayName,
            player.PrivateClue.ToView(),
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
        // Built inside the worker: the ledger and open orders are only safe to read there.
        var view = await room.Ask(r =>
        {
            if (!r.IsPlayer(traderKey))
            {
                return null;
            }

            var player = r.GetPlayer(traderKey);
            var account = r.Ledger.Get(traderKey);
            return new AccountView(
                player.DisplayName,
                player.PrivateClue.ToView(),
                account.Cash,
                account.Position,
                r.OpenOrdersFor(traderKey).Select(o => o.ToView()).ToList());
        });

        return view is null ? Results.Unauthorized() : Results.Ok(view);
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
            await group.SendAsync(MarketEvents.BookUpdated, o.Snapshot);
            if (o.Result.Trades.Count > 0)
            {
                var tape = o.Result.Trades.Select(t => t.ToPublic()).ToList();
                await group.SendAsync(MarketEvents.TradesExecuted, tape);
            }
        }

        // A rejected order is a normal business outcome (like an exchange reject message), so it is
        // 200 OK with Status = Rejected. 400 is reserved for requests that are malformed.
        return Results.Ok(o.Result.ToResponse());
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

        await hub.Clients.Group(MarketHub.GroupFor(room.Code)).SendAsync(MarketEvents.BookUpdated, o.Snapshot);
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

        var view = clue.ToView();
        await hub.Clients.Group(MarketHub.GroupFor(room.Code)).SendAsync(MarketEvents.ClueRevealed, view);
        return Results.Ok(view);
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

        var (settlement, snapshot) = await room.Ask(r => (r.Settle().ToView(), r.Snapshot()));
        var group = hub.Clients.Group(MarketHub.GroupFor(room.Code));
        await group.SendAsync(MarketEvents.RoundSettled, settlement);
        await group.SendAsync(MarketEvents.BookUpdated, snapshot);
        return Results.Ok(settlement);
    }

    private sealed record JoinOutcome(Player? Player, string? Error, int Count);
}
