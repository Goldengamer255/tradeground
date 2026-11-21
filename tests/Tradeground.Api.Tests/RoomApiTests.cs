using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Tradeground.Api.Endpoints;
using Tradeground.Engine;
using Tradeground.Game;

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
        aliceAccount.ShouldBe(new AccountView(10_150, -5));

        var settlement = await SendAsync<Settlement>(client, HttpMethod.Post, $"/api/rooms/{room.Code}/settlement", hostKey: room.HostKey);
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
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/market"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        var update = new TaskCompletionSource<BookSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<BookSnapshot>("bookUpdated", snapshot => update.TrySetResult(snapshot));
        await connection.StartAsync();

        var initial = await connection.InvokeAsync<BookSnapshot>("Subscribe", room.Code);
        initial.Bids.ShouldBeEmpty();

        await PlaceOrderAsync(client, room.Code, alice.TraderKey, new PlaceOrderRequest(Side.Buy, OrderType.Limit, 3, 25));

        var pushed = await update.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pushed.Bids.ShouldHaveSingleItem().ShouldBe(new DepthLevel(25, 3, 1));
        pushed.LastSequence.ShouldBeGreaterThan(initial.LastSequence);
    }

    private static Task<CreateRoomResponse> CreateRoomAsync(HttpClient client) =>
        SendAsync<CreateRoomResponse>(client, HttpMethod.Post, "/api/rooms", new CreateRoomRequest("trader-dice", Seed: 42));

    private static Task<JoinRoomResponse> JoinAsync(HttpClient client, string code) =>
        SendAsync<JoinRoomResponse>(client, HttpMethod.Post, $"/api/rooms/{code}/players");

    private static Task<PlaceOrderResponse> PlaceOrderAsync(HttpClient client, string code, string traderKey, PlaceOrderRequest order) =>
        SendAsync<PlaceOrderResponse>(client, HttpMethod.Post, $"/api/rooms/{code}/orders", order, traderKey: traderKey);

    private static async Task<T> SendAsync<T>(
        HttpClient client, HttpMethod method, string url, object? body = null, string? traderKey = null, string? hostKey = null)
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
            request.Headers.Add(RoomEndpoints.TraderKeyHeader, traderKey);
        }

        if (hostKey is not null)
        {
            request.Headers.Add(RoomEndpoints.HostKeyHeader, hostKey);
        }

        return await client.SendAsync(request);
    }
}
