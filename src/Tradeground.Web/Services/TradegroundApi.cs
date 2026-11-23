using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tradeground.Contracts;

namespace Tradeground.Web.Services;

/// <summary>
/// The result of one API call. Nothing throws: a failed call carries a message a player can act on.
/// </summary>
public sealed record ApiResult<T>(bool Ok, HttpStatusCode Status, T? Data, string? Error)
{
    public static ApiResult<T> Success(HttpStatusCode status, T? data) => new(true, status, data, null);

    public static ApiResult<T> Failure(HttpStatusCode status, string error) => new(false, status, default, error);
}

/// <summary>
/// Every call the browser app makes to the API, in one place. Request and response shapes come from
/// Tradeground.Contracts, the same types the server uses, so the two sides can't disagree.
/// </summary>
public sealed class TradegroundApi(HttpClient http)
{
    private static readonly TradegroundJsonContext Json = TradegroundJsonContext.Default;

    // ---------- Market Maker ----------

    public Task<ApiResult<ModeSummary[]>> GetModes() => Send(HttpMethod.Get, "api/modes", Json.ModeSummaryArray);

    public Task<ApiResult<CreateRoomResponse>> CreateRoom(string modeId) =>
        Send(HttpMethod.Post, "api/rooms", Json.CreateRoomResponse, Body(new CreateRoomRequest(modeId), Json.CreateRoomRequest));

    public Task<ApiResult<RoomView>> GetRoom(string code) => Send(HttpMethod.Get, Room(code), Json.RoomView);

    public Task<ApiResult<JoinRoomResponse>> JoinRoom(string code) =>
        Send(HttpMethod.Post, $"{Room(code)}/players", Json.JoinRoomResponse);

    public Task<ApiResult<AccountView>> GetMe(string code, string traderKey) =>
        Send(HttpMethod.Get, $"{Room(code)}/me", Json.AccountView, headers: (ApiHeaders.TraderKey, traderKey));

    public Task<ApiResult<PlaceOrderResponse>> PlaceOrder(string code, string traderKey, PlaceOrderRequest order) =>
        Send(HttpMethod.Post, $"{Room(code)}/orders", Json.PlaceOrderResponse, Body(order, Json.PlaceOrderRequest), (ApiHeaders.TraderKey, traderKey));

    public Task<ApiResult<string>> CancelOrder(string code, string traderKey, long orderId) =>
        Send(HttpMethod.Delete, $"{Room(code)}/orders/{orderId}", Json.String, headers: (ApiHeaders.TraderKey, traderKey));

    public Task<ApiResult<ClueView>> RevealClue(string code, string hostKey) =>
        Send(HttpMethod.Post, $"{Room(code)}/clues", Json.ClueView, headers: (ApiHeaders.HostKey, hostKey));

    public Task<ApiResult<SettlementView>> Settle(string code, string hostKey) =>
        Send(HttpMethod.Post, $"{Room(code)}/settlement", Json.SettlementView, headers: (ApiHeaders.HostKey, hostKey));

    // ---------- Up or Down ----------

    public Task<ApiResult<TimeframeView[]>> GetTimeframes() => Send(HttpMethod.Get, "api/charts/timeframes", Json.TimeframeViewArray);

    public Task<ApiResult<ChartHighlight[]>> GetHighlights() => Send(HttpMethod.Get, "api/charts/highlights", Json.ChartHighlightArray);

    public Task<ApiResult<ChartStarted>> StartChart(string timeframe) =>
        Send(HttpMethod.Post, "api/charts", Json.ChartStarted, Body(new TimeframeRequest(timeframe), Json.TimeframeRequest));

    public Task<ApiResult<ChartView>> GetChart(string key, string timeframe) =>
        Send(HttpMethod.Get, $"api/charts/mine?timeframe={Uri.EscapeDataString(timeframe)}", Json.ChartView, headers: (ApiHeaders.ChartKey, key));

    public Task<ApiResult<ChartView>> NextChart(string key, string timeframe) =>
        Send(HttpMethod.Post, "api/charts/mine/next", Json.ChartView, Body(new TimeframeRequest(timeframe), Json.TimeframeRequest), (ApiHeaders.ChartKey, key));

    public Task<ApiResult<ChartView>> CallChart(string key, Direction direction, string timeframe) =>
        Send(HttpMethod.Post, "api/charts/mine/call", Json.ChartView, Body(new CallRequest(direction, timeframe), Json.CallRequest), (ApiHeaders.ChartKey, key));

    // ---------- Plumbing ----------

    private static string Room(string code) => $"api/rooms/{Uri.EscapeDataString(code)}";

    private static JsonContent Body<T>(T value, JsonTypeInfo<T> type) => JsonContent.Create(value, type);

    private async Task<ApiResult<T>> Send<T>(
        HttpMethod method,
        string url,
        JsonTypeInfo<T> type,
        HttpContent? body = null,
        (string Name, string Value)? headers = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body };
        if (headers is { } h)
        {
            request.Headers.Add(h.Name, h.Value);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            return ApiResult<T>.Failure(0, "Can't reach the server. Check your connection and try again.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                var data = string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize(text, type);
                return ApiResult<T>.Success(response.StatusCode, data);
            }

            return ApiResult<T>.Failure(response.StatusCode, MessageFor(response.StatusCode, text));
        }
    }

    /// <summary>
    /// The server answers errors as a plain JSON string ("The round is over.") or as ProblemDetails
    /// ({ "errors": ..., "detail": ... }). Turn either into one readable sentence.
    /// </summary>
    private static string MessageFor(HttpStatusCode status, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String && root.GetString() is { Length: > 0 } message)
            {
                return message;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in errors.EnumerateObject())
                    {
                        if (field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() > 0)
                        {
                            return field.Value[0].GetString() ?? "That request wasn't valid.";
                        }
                    }
                }

                if (root.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } d)
                {
                    return d;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through to a message based on the status code.
        }

        return status switch
        {
            HttpStatusCode.NotFound => "That room doesn't exist. Check the code with your host.",
            HttpStatusCode.Unauthorized => "This page doesn't have a seat in that room any more.",
            HttpStatusCode.ServiceUnavailable => "The server is busy. Try again in a moment.",
            _ => $"The server answered {(int)status}.",
        };
    }
}
