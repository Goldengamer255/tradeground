using Tradeground.Engine;

namespace Tradeground.Game;

public sealed class Account(string traderId, long cash)
{
    public string TraderId { get; } = traderId;
    public long Cash { get; internal set; } = cash;

    /// <summary>Positive = long (owns contracts), negative = short (owes contracts).</summary>
    public long Position { get; internal set; }
}

/// <summary>
/// Double-entry bookkeeping for cash and contracts: every trade debits one account and
/// credits another by exactly the same amount, so totals across all accounts never change.
/// </summary>
public sealed class Ledger(long startingCash)
{
    private readonly Dictionary<string, Account> _accounts = [];

    public long StartingCash { get; } = startingCash;

    public IReadOnlyCollection<Account> Accounts => _accounts.Values;

    /// <summary>Invariant: always equals StartingCash x number of accounts.</summary>
    public long TotalCash => _accounts.Values.Sum(a => a.Cash);

    /// <summary>Invariant: always zero. Every contract someone is long, someone else is short.</summary>
    public long TotalPosition => _accounts.Values.Sum(a => a.Position);

    public Account Open(string traderId)
    {
        if (_accounts.ContainsKey(traderId))
        {
            throw new InvalidOperationException($"Account {traderId} already exists.");
        }

        var account = new Account(traderId, StartingCash);
        _accounts.Add(traderId, account);
        return account;
    }

    public Account Get(string traderId) => _accounts[traderId];

    public void Apply(Trade trade)
    {
        var notional = checked(trade.Price * trade.Quantity);
        var buyer = _accounts[trade.BuyerId];
        var seller = _accounts[trade.SellerId];

        buyer.Cash -= notional;
        buyer.Position += trade.Quantity;
        seller.Cash += notional;
        seller.Position -= trade.Quantity;
    }
}
