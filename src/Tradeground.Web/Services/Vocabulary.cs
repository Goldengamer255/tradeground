using Tradeground.Contracts;

namespace Tradeground.Web.Services;

/// <summary>The same market, two vocabularies. Explorer players learn the idea before the jargon.</summary>
public sealed record Vocabulary(
    string Book,
    string BookHint,
    string Bids,
    string Asks,
    string Cash,
    string Position,
    string AtFair,
    string Die,
    string Dice,
    string ClueTitle,
    string News,
    string NoNews,
    string Trades,
    string NoTrades,
    string Price,
    string Quantity,
    string Estimate,
    bool MarketOrders,
    string TicketTitle,
    string OrdersTitle,
    string NoOrders)
{
    public static Vocabulary For(Audience audience) => audience == Audience.Explorer ? Explorer : Trader;

    public static readonly Vocabulary Explorer = new(
        Book: "Offers",
        BookHint: "Tap an offer to fill your order at that price",
        Bids: "Buyers",
        Asks: "Sellers",
        Cash: "Coins",
        Position: "You own",
        AtFair: "If it ended now",
        Die: "crate",
        Dice: "crates",
        ClueTitle: "Your peek",
        News: "Breaking news",
        NoNews: "No news yet. Your host will open crates during the round.",
        Trades: "Sales",
        NoTrades: "Nothing sold yet.",
        Price: "Price in coins",
        Quantity: "How many",
        Estimate: "Fair price guess",
        MarketOrders: false,
        TicketTitle: "Make an offer",
        OrdersTitle: "Your offers",
        NoOrders: "When nobody accepts your offer straight away, it waits here until someone does.");

    public static readonly Vocabulary Trader = new(
        Book: "Order book",
        BookHint: "Click a level to trade at its price",
        Bids: "Bids",
        Asks: "Asks",
        Cash: "Cash",
        Position: "Position",
        AtFair: "P&L at fair value",
        Die: "die",
        Dice: "dice",
        ClueTitle: "Your clue",
        News: "News",
        NoNews: "No news yet. The host reveals dice during the round.",
        Trades: "Trades",
        NoTrades: "No trades yet.",
        Price: "Limit price",
        Quantity: "Quantity",
        Estimate: "Fair value",
        MarketOrders: true,
        TicketTitle: "New order",
        OrdersTitle: "Resting orders",
        NoOrders: "Limit orders that don't trade straight away rest on the book and show up here.");
}
