namespace Tradeground.Contracts;

/// <summary>Header names both sides must spell the same way.</summary>
public static class ApiHeaders
{
    /// <summary>A player's secret seat key in a Market Maker room.</summary>
    public const string TraderKey = "X-Trader-Key";

    /// <summary>The host's secret key for a Market Maker room.</summary>
    public const string HostKey = "X-Host-Key";

    /// <summary>The secret key for an Up or Down run.</summary>
    public const string ChartKey = "X-Chart-Key";
}

/// <summary>The SignalR hub and the names of the events it pushes.</summary>
public static class MarketEvents
{
    public const string HubPath = "/hubs/market";

    /// <summary>Hub method: join a room's live updates; returns the current book.</summary>
    public const string Subscribe = "Subscribe";

    /// <summary>A new <see cref="Engine.BookSnapshot"/>.</summary>
    public const string BookUpdated = "bookUpdated";

    /// <summary>A list of <see cref="PublicTrade"/>.</summary>
    public const string TradesExecuted = "tradesExecuted";

    /// <summary>A <see cref="ClueView"/>.</summary>
    public const string ClueRevealed = "clueRevealed";

    /// <summary>The new player count.</summary>
    public const string PlayerJoined = "playerJoined";

    /// <summary>A <see cref="SettlementView"/>.</summary>
    public const string RoundSettled = "roundSettled";
}
