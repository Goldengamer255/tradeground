using Tradeground.Engine;

namespace Tradeground.Game.Tests;

public sealed class GameRoomTests
{
    // trader-dice: starting cash 10,000, max position 50, max order 20.
    private readonly GameRoom _room = new(TestModes.TraderDice(), seed: 42);

    public GameRoomTests()
    {
        _room.Join("alice", "Alice");
        _room.Join("bob", "Bob");
    }

    [Fact]
    public void A_trade_moves_cash_and_contracts_between_players()
    {
        _room.Submit(OrderRequest.Limit("alice", Side.Sell, price: 30, quantity: 5));
        _room.Submit(OrderRequest.Market("bob", Side.Buy, quantity: 5));

        var alice = _room.Ledger.Get("alice");
        var bob = _room.Ledger.Get("bob");
        (alice.Cash, alice.Position).ShouldBe((10_150L, -5L));
        (bob.Cash, bob.Position).ShouldBe((9_850L, 5L));
    }

    [Fact]
    public void Risk_check_counts_open_orders_not_just_the_current_position()
    {
        _room.Submit(OrderRequest.Limit("alice", Side.Buy, price: 30, quantity: 20)).Status.ShouldBe(OrderStatus.Resting);
        _room.Submit(OrderRequest.Limit("alice", Side.Buy, price: 30, quantity: 20)).Status.ShouldBe(OrderStatus.Resting);

        var third = _room.Submit(OrderRequest.Limit("alice", Side.Buy, price: 30, quantity: 20));

        third.Status.ShouldBe(OrderStatus.Rejected, "40 open + 20 more could reach 60, over the limit of 50");
        third.RejectReason.ShouldNotBeNull().ShouldContain("Position limit");
        _room.Submit(OrderRequest.Limit("alice", Side.Buy, price: 30, quantity: 10)).Status.ShouldBe(OrderStatus.Resting);
    }

    [Fact]
    public void Cancelling_frees_up_risk_capacity()
    {
        var first = _room.Submit(OrderRequest.Limit("alice", Side.Sell, price: 40, quantity: 20));
        _room.Submit(OrderRequest.Limit("alice", Side.Sell, price: 40, quantity: 20));
        _room.Submit(OrderRequest.Limit("alice", Side.Sell, price: 40, quantity: 20)).Status.ShouldBe(OrderStatus.Rejected);

        _room.Cancel("alice", first.OrderId).ShouldBeTrue();

        _room.Submit(OrderRequest.Limit("alice", Side.Sell, price: 40, quantity: 20)).Status.ShouldBe(OrderStatus.Resting);
    }

    [Fact]
    public void Settlement_closes_positions_at_the_true_value_and_is_zero_sum()
    {
        _room.Submit(OrderRequest.Limit("alice", Side.Sell, price: 30, quantity: 5));
        _room.Submit(OrderRequest.Market("bob", Side.Buy, quantity: 5));

        var settlement = _room.Settle();

        var tv = settlement.TrueValue;
        settlement.Results.Single(r => r.DisplayName == "Alice").ProfitAndLoss.ShouldBe(150 - (5 * tv));
        settlement.Results.Single(r => r.DisplayName == "Bob").ProfitAndLoss.ShouldBe((5 * tv) - 150);
        settlement.Results.Sum(r => r.ProfitAndLoss).ShouldBe(0);
        settlement.Results.Select(r => r.Rank).ShouldBe(new[] { 1, 2 });
    }

    [Fact]
    public void Settlement_cancels_resting_orders_and_reveals_every_die()
    {
        _room.Submit(OrderRequest.Limit("alice", Side.Sell, price: 36, quantity: 5));
        _room.Submit(OrderRequest.Limit("bob", Side.Buy, price: 30, quantity: 5));

        var settlement = _room.Settle();

        _room.Snapshot().Bids.ShouldBeEmpty("the contract expired, so nothing is left to trade");
        _room.Snapshot().Asks.ShouldBeEmpty();
        _room.OpenOrdersFor("alice").ShouldBeEmpty();
        settlement.Dice.Count.ShouldBe(10);
        settlement.Dice.Sum().ShouldBe((int)settlement.TrueValue, "trader-dice is worth 1 per pip");
    }

    [Fact]
    public void No_trading_after_settlement()
    {
        _room.Settle();

        _room.Submit(OrderRequest.Limit("alice", Side.Buy, price: 30, quantity: 1)).Status.ShouldBe(OrderStatus.Rejected);
        _room.Phase.ShouldBe(RoomPhase.Settled);
    }

    [Fact]
    public void Open_orders_show_what_is_still_resting_after_partial_fills()
    {
        var resting = _room.Submit(OrderRequest.Limit("alice", Side.Sell, price: 36, quantity: 10));
        _room.Submit(OrderRequest.Market("bob", Side.Buy, quantity: 4));

        var open = _room.OpenOrdersFor("alice").ShouldHaveSingleItem();

        (open.Id, open.Price, open.Remaining).ShouldBe((resting.OrderId, 36L, 6L));
        _room.OpenOrdersFor("bob").ShouldBeEmpty();
    }

    [Fact]
    public void The_tape_records_every_trade_in_order()
    {
        _room.Submit(OrderRequest.Limit("alice", Side.Sell, price: 36, quantity: 1));
        _room.Submit(OrderRequest.Limit("alice", Side.Sell, price: 37, quantity: 1));
        _room.Submit(OrderRequest.Market("bob", Side.Buy, quantity: 2));

        _room.Tape.Select(t => t.Price).ShouldBe(new[] { 36L, 37L });
    }

    [Fact]
    public void Display_names_are_unique_within_a_room()
    {
        Should.Throw<InvalidOperationException>(() => _room.Join("carol", "Alice"));
        _room.IsNameTaken("Alice").ShouldBeTrue();
        _room.IsPlayer("carol").ShouldBeFalse("a failed join must not leave a half-created player");
    }

    [Fact]
    public void Strangers_cannot_trade()
    {
        _room.Submit(OrderRequest.Limit("mallory", Side.Buy, price: 30, quantity: 1)).RejectReason.ShouldBe("Unknown player.");
    }
}
