using Microsoft.JSInterop;

namespace Tradeground.Web.Services;

/// <summary>
/// sessionStorage (one per tab, survives a refresh) and localStorage (shared by all tabs, survives
/// closing the browser), through the small JavaScript helper. Storage can be blocked; the helper
/// swallows that, so these simply return null.
/// </summary>
public sealed class BrowserStorage(IJSRuntime js)
{
    public ValueTask<string?> GetSession(string key) => js.InvokeAsync<string?>("tradeground.storageGet", "sessionStorage", key);

    public ValueTask SetSession(string key, string? value) => js.InvokeVoidAsync("tradeground.storageSet", "sessionStorage", key, value);

    public ValueTask<string?> GetLocal(string key) => js.InvokeAsync<string?>("tradeground.storageGet", "localStorage", key);

    public ValueTask SetLocal(string key, string? value) => js.InvokeVoidAsync("tradeground.storageSet", "localStorage", key, value);
}

/// <summary>Which role this browser tab has in a Market Maker room, and its secret key.</summary>
public sealed record Seat(SeatRole Role, string Key);

public enum SeatRole
{
    Player,
    Host,
}

/// <summary>
/// Seats in Market Maker rooms. Player seats live in sessionStorage: one per tab, so two players can
/// share a computer, and a refresh keeps the seat. Host seats live in localStorage, so closing the
/// host's tab by accident doesn't lose control of the room.
/// </summary>
public sealed class Seats(BrowserStorage storage)
{
    private static string Key(string code) => $"tradeground.seat.{code}";

    public async Task<Seat?> Find(string code)
    {
        if (await storage.GetSession(Key(code)) is { Length: > 0 } player)
        {
            return new Seat(SeatRole.Player, player);
        }

        return await storage.GetLocal(Key(code)) is { Length: > 0 } host ? new Seat(SeatRole.Host, host) : null;
    }

    public async Task SavePlayer(string code, string traderKey) => await storage.SetSession(Key(code), traderKey);

    public async Task SaveHost(string code, string hostKey) => await storage.SetLocal(Key(code), hostKey);

    public async Task ForgetPlayer(string code) => await storage.SetSession(Key(code), null);
}
