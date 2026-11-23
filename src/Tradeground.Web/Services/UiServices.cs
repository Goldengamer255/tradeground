namespace Tradeground.Web.Services;

/// <summary>Short messages at the bottom of the screen. Any page can raise one; the layout shows it.</summary>
public sealed class Toasts
{
    public event Action<string>? Shown;

    public void Show(string message) => Shown?.Invoke(message);
}

/// <summary>One "How to play" guide: written for someone who has never traded.</summary>
public sealed record Guide(string Title, string Intro, IReadOnlyList<string> Steps, string Note);

/// <summary>
/// The "How to play" guides and the dialog that shows them. Any page can open a guide; the layout
/// owns the single dialog. First-time players see a game's guide automatically, once per browser.
/// </summary>
public sealed class HowTo(BrowserStorage storage)
{
    public const string UpOrDown = "up-or-down";
    public const string MarketMaker = "market-maker";

    public static readonly IReadOnlyDictionary<string, Guide> Guides = new Dictionary<string, Guide>
    {
        [UpOrDown] = new(
            "How to play Up or Down",
            "You see a real crypto price chart from the past, stopped at \"now\". Guess where the price goes next.",
            [
                "Use the timeframes to zoom in and out. It's the same chart every time: on \"1m\" each candle is one minute, on \"4h\" each is four hours. Zoom out to see the big trend, zoom in to see the last hour.",
                "Read the chart. Each candle is one slice of time: blue means the price went up during it, orange means it went down. The thin line shows the highest and lowest price in that slice.",
                "Press Up if you think the price will be higher 20 candles from now, or Down if lower. Your call uses the timeframe you're looking at, so on \"1m\" that's 20 minutes ahead and on \"4h\" it's 3 days and 8 hours.",
                "Watch the next 20 candles play out. Then you find out if you were right, and which market and dates it was. You can still switch timeframes to see what happened at every zoom level.",
            ],
            "Nobody gets this right every time, including professionals. Pay attention to how often it feels like a coin flip: that is a real lesson about markets."),
        [MarketMaker] = new(
            "How to play Market Maker",
            "Picture an airport currency booth. It buys dollars for 1.60 and sells them for 1.70, and earns the gap in between. In this game, you are the booth.",
            [
                "Everyone trades the same thing, and its true value is hidden: it is the total of some dice (in Lemonade Stand, the lemons in some crates).",
                "You secretly see one die. The others average 3.5 each, so you can estimate the total. The \"fair value\" number on your screen does that sum for you.",
                "Buy when someone will sell for less than fair value; sell when someone will pay more. Put your own prices on the order book, or click someone else's price to trade with them.",
                "The host reveals dice as \"news\" during the round. Your estimate gets better, so change your prices to match.",
                "When the round ends, all the dice are shown. Everything you hold is worth the true total, and the player with the most profit wins.",
            ],
            "You need at least two players: one person hosts on a big screen, everyone else joins with the room code. Computer traders, so you can play alone, are coming next."),
    };

    public event Action<Guide>? Opened;

    public void Show(string gameId)
    {
        if (Guides.TryGetValue(gameId, out var guide))
        {
            Opened?.Invoke(guide);
        }
    }

    public async Task ShowFirstTime(string gameId)
    {
        var key = $"tradeground.seen.{gameId}";
        if (await storage.GetLocal(key) is not null)
        {
            return;
        }

        await storage.SetLocal(key, "1");
        Show(gameId);
    }
}
