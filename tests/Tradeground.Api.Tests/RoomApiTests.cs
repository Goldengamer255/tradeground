using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Tradeground.Contracts;
using Tradeground.Engine;

namespace Tradeground.Api.Tests;

/// <summary>
/// Integration tests: WebApplicationFactory boots the REAL app (routing, JSON, DI, SignalR)
/// in memory, and these tests talk to it over HTTP exactly like a browser would.
/// </summary>
public sealed class RoomApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lists_the_built_in_game_modes()
    {
        using var client = factory.CreateClient();

        var modes = await client.GetFromJsonAsync<ModeSummary[]>("/api/modes", Json);

        modes.ShouldNotBeNull().Select(m => m.Id).ShouldBe(new[] { "explorer-lemonade", "trader-dice" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Two_players_trade_and_the_round_settles_to_zero_sum()
    {
        using var client = factory.CreateClient();
        var room = await CreateRoomAsync(client);
        var alice = await JoinAsync(client, room.Code);
        var bob = await JoinAsync(client, room.Code);

        var sell = await PlaceOrderAsync(client, room.Code, alice.TraderKey, new PlaceOrderRequest(Side.Sell, OrderType.Limit, 5, 30));
        var buy = await PlaceOrderAsync(client, room.Code, bob.TraderKey, new PlaceOrderRequest(Side.Buy, OrderType.Market, 5, null));

        sell.Status.ShouldBe(OrderStatus.Resting);
        buy.Status.ShouldBe(OrderStatus.Filled);
        buy.Fills.ShouldHaveSingleItem().ShouldBe(new Fill(30, 5));

        var aliceAccount = await SendAsync<AccountView>(client, HttpMethod.Get, $"/api/rooms/{room.Code}/me", traderKey: alice.TraderKey);
        (aliceAccount.Cash, aliceAccount.Position).ShouldBe((10_150L, -5L));
        aliceAccount.DisplayName.ShouldBe(alice.DisplayName);
        aliceAccount.OpenOrders.ShouldBeEmpty("her sell order was completely filled");

        var settlement = await SendAsync<SettlementView>(client, HttpMethod.Post, $"/api/rooms/{room.Code}/settlement", hostKey: room.HostKey);
        var tv = settlement.TrueValue;
        settlement.Results.Sum(r => r.ProfitAndLoss).ShouldBe(0);
        settlement.Results.Select(r => r.ProfitAndLoss).ShouldBe(new[] { 150 - (5 * tv), (5 * tv) - 150 }, ignoreOrder: true);
    }

    [Fact]
    public async Task Orders_need_a_valid_trader_key()
    {
        using var client = factory.CreateClient();
        var room = await CreateRoomAsync(client);

        using var response = await SendRawAsync(
            client, HttpMethod.Post, $"/api/rooms/{room.Code}/orders",
            new PlaceOrderRequest(Side.Buy, OrderType.Limit, 1, 30), traderKey: "not-a-real-key");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Only_the_host_can_settle_the_round()
    {
        using var client = factory.CreateClient();
        var room = await CreateRoomAsync(client);
        var player = await JoinAsync(client, room.Code);

        using var response = await SendRawAsync(
            client, HttpMethod.Post, $"/api/rooms/{room.Code}/settlement", body: null, hostKey: player.TraderKey);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_rooms_and_modes_are_reported_clearly()
    {
        using var client = factory.CreateClient();

        using var noRoom = await SendRawAsync(client, HttpMethod.Get, "/api/rooms/ZZZZZZ/book");
        using var noMode = await SendRawAsync(client, HttpMethod.Post, "/api/rooms", new CreateRoomRequest("no-such-mode", null));

        noRoom.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        noMode.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Subscribers_receive_live_order_book_updates()
    {
        using var client = factory.CreateClient();
        var room = await CreateRoomAsync(client);
        var alice = await JoinAsync(client, room.Code);

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, MarketEvents.HubPath.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        var update = new TaskCompletionSource<BookSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<BookSnapshot>(MarketEvents.BookUpdated, snapshot => update.TrySetResult(snapshot));
        await connection.StartAsync();

        var initial = await connection.InvokeAsync<BookSnapshot>(MarketEvents.Subscribe, room.Code);
        initial.Bids.ShouldBeEmpty();

        await PlaceOrderAsync(client, room.Code, alice.TraderKey, new PlaceOrderRequest(Side.Buy, OrderType.Limit, 3, 25));

        var pushed = await update.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pushed.Bids.ShouldHaveSingleItem().ShouldBe(new DepthLevel(25, 3, 1));
        pushed.LastSequence.ShouldBeGreaterThan(initial.LastSequence);
    }

    [Fact]
    public async Task A_player_can_see_and_cancel_their_resting_orders()
    {
        using var client = factory.CreateClient();
        var room = await CreateRoomAsync(client);
        var alice = await JoinAsync(client, room.Code);
        var placed = await PlaceOrderAsync(client, room.Code, alice.TraderKey, new PlaceOrderRequest(Side.Buy, OrderType.Limit, 4, 31));

        var before = await SendAsync<AccountView>(client, HttpMethod.Get, $"/api/rooms/{room.Code}/me", traderKey: alice.TraderKey);
        before.OpenOrders.ShouldHaveSingleItem().ShouldBe(new OpenOrderView(placed.OrderId, Side.Buy, 31, 4));

        using var cancel = await SendRawAsync(client, HttpMethod.Delete, $"/api/rooms/{room.Code}/orders/{placed.OrderId}", traderKey: alice.TraderKey);
        cancel.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await SendAsync<AccountView>(client, HttpMethod.Get, $"/api/rooms/{room.Code}/me", traderKey: alice.TraderKey);
        after.OpenOrders.ShouldBeEmpty();
    }

    [Fact]
    public async Task Room_view_lets_a_late_joiner_catch_up()
    {
        using var client = factory.CreateClient();
        var room = await CreateRoomAsync(client);
        var alice = await JoinAsync(client, room.Code);
        var bob = await JoinAsync(client, room.Code);
        await PlaceOrderAsync(client, room.Code, alice.TraderKey, new PlaceOrderRequest(Side.Sell, OrderType.Limit, 2, 36));
        await PlaceOrderAsync(client, room.Code, bob.TraderKey, new PlaceOrderRequest(Side.Buy, OrderType.Market, 2, null));
        var news = await SendAsync<ClueView>(client, HttpMethod.Post, $"/api/rooms/{room.Code}/clues", hostKey: room.HostKey);

        var view = await SendAsync<RoomView>(client, HttpMethod.Get, $"/api/rooms/{room.Code}");

        view.Phase.ShouldBe(RoomPhase.Trading);
        view.PlayerCount.ShouldBe(2);
        view.Mode.DiceCount.ShouldBe(10);
        view.PublicClues.ShouldHaveSingleItem().ShouldBe(news);
        view.RecentTrades.ShouldHaveSingleItem().ShouldBe(new PublicTrade(view.RecentTrades[0].Sequence, 36, 2, Side.Buy));
        view.Settlement.ShouldBeNull();
    }

    [Fact]
    public async Task Players_in_a_room_always_get_different_names()
    {
        using var client = factory.CreateClient();
        var room = await CreateRoomAsync(client);

        var players = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => JoinAsync(client, room.Code)));

        players.Select(p => p.DisplayName).Distinct().Count().ShouldBe(40);
    }

    private static Task<CreateRoomResponse> CreateRoomAsync(HttpClient client) =>
        SendAsync<CreateRoomResponse>(client, HttpMethod.Post, "/api/rooms", new CreateRoomRequest("trader-dice", Seed: 42));

    private static Task<JoinRoomResponse> JoinAsync(HttpClient client, string code) =>
        SendAsync<JoinRoomResponse>(client, HttpMethod.Post, $"/api/rooms/{code}/players");

    private static Task<PlaceOrderResponse> PlaceOrderAsync(HttpClient client, string code, string traderKey, PlaceOrderRequest order) =>
        SendAsync<PlaceOrderResponse>(client, HttpMethod.Post, $"/api/rooms/{code}/orders", order, traderKey: traderKey);

    private static async Task<T> SendAsync<T>(
        HttpClient client, HttpMethod method, string url, object? body = null, string? traderKey = null, string? hostKey = null)
        where T : class
    {
        using var response = await SendRawAsync(client, method, url, body, traderKey, hostKey);
        response.IsSuccessStatusCode.ShouldBeTrue($"{method} {url} returned {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<T>(Json)).ShouldNotBeNull();
    }

    private static async Task<HttpResponseMessage> SendRawAsync(
        HttpClient client, HttpMethod method, string url, object? body = null, string? traderKey = null, string? hostKey = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        if (traderKey is not null)
        {
            request.Headers.Add(ApiHeaders.TraderKey, traderKey);
        }

        if (hostKey is not null)
        {
            request.Headers.Add(ApiHeaders.HostKey, hostKey);
        }

        return await client.SendAsync(request);
    }
}
