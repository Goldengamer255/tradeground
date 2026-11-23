using Microsoft.AspNetCore.SignalR;
using Tradeground.Api.Rooms;
using Tradeground.Engine;

namespace Tradeground.Api.Hubs;

/// <summary>
/// Real-time channel. Clients subscribe to a room and the server pushes the events named in
/// <see cref="Contracts.MarketEvents"/>: book updates, trades, news, new players and the settlement.
/// </summary>
public sealed class MarketHub(RoomRegistry rooms) : Hub
{
    public static string GroupFor(string code) => $"room:{code.ToUpperInvariant()}";

    /// <summary>Joins the room's broadcast group and returns the current book so the client starts in sync.</summary>
    public async Task<BookSnapshot> Subscribe(string code)
    {
        if (!rooms.TryGet(code, out var room))
        {
            throw new HubException("Room not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(room.Code));
        return await room.Ask(r => r.Snapshot());
    }
}
