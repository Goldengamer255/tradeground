namespace Tradeground.Engine.Tests;

/// <summary>Example-based tests: one specific scenario each, named as a rule of the market.</summary>
public sealed class OrderBookTests
{
    private readonly OrderBook _book = new("TEST", maxOrderQuantity: 1_000);

    [Fact]
    public void Limit_order_with_nothing_to_match_rests_on_the_book()
    {
        var result = _book.Submit(OrderRequest.Limit("alice", Side.Buy, price: 50, quantity: 10));

        result.Status.ShouldBe(OrderStatus.Resting);
        result.Trades.ShouldBeEmpty();
        result.RestingQuantity.ShouldBe(10);
        _book.BestBid.ShouldBe(50);
        _book.BestAsk.ShouldBe(null);
    }

    [Fact]
    public void Trades_execute_at_the_resting_orders_price()
    {
        _book.Submit(OrderRequest.Limit("alice", Side.Sell, price: 50, quantity: 5));

        var result = _book.Submit(OrderRequest.Limit("bob", Side.Buy, price: 55, quantity: 5));

        result.Status.ShouldBe(OrderStatus.Filled);
        var trade = result.Trades.ShouldHaveSingleItem();
        trade.Price.ShouldBe(50, "the maker set the price; the taker accepted it");
        trade.BuyerId.ShouldBe("bob");
        trade.SellerId.ShouldBe("alice");
        trade.AggressorSide.ShouldBe(Side.Buy);
    }

    [Fact]
    public void Better_prices_match_first()
    {
        _book.Submit(OrderRequest.Limit("expensive", Side.Sell, price: 52, quantity: 1));
        _book.Submit(OrderRequest.Limit("cheap", Side.Sell, price: 50, quantity: 1));

        var result = _book.Submit(OrderRequest.Limit("bob", Side.Buy, price: 60, quantity: 1));

        result.Trades.ShouldHaveSingleItem().SellerId.ShouldBe("cheap");
    }

    [Fact]
    public void At_the_same_price_the_earlier_order_matches_first()
    {
        _book.Submit(OrderRequest.Limit("first", Side.Sell, price: 50, quantity: 1));
        _book.Submit(OrderRequest.Limit("second", Side.Sell, price: 50, quantity: 1));

        var result = _book.Submit(OrderRequest.Limit("bob", Side.Buy, price: 50, quantity: 1));

        result.Trades.ShouldHaveSingleItem().SellerId.ShouldBe("first");
    }

    [Fact]
    public void Partially_filled_limit_order_rests_its_remainder()
    {
        _book.Submit(OrderRequest.Limit("alice", Side.Sell, price: 50, quantity: 3));

        var result = _book.Submit(OrderRequest.Limit("bob", Side.Buy, price: 50, quantity: 5));

        result.Status.ShouldBe(OrderStatus.PartiallyFilled);
        result.FilledQuantity.ShouldBe(3);
        result.RestingQuantity.ShouldBe(2);
        _book.BestBid.ShouldBe(50);
        _book.BestAsk.ShouldBe(null);
    }

    [Fact]
    public void Market_order_sweeps_through_price_levels()
    {
        _book.Submit(OrderRequest.Limit("a", Side.Sell, price: 50, quantity: 1));
        _book.Submit(OrderRequest.Limit("b", Side.Sell, price: 51, quantity: 2));
        _book.Submit(OrderRequest.Limit("c", Side.Sell, price: 52, quantity: 3));

        var result = _book.Submit(OrderRequest.Market("bob", Side.Buy, quantity: 4));

        result.Status.ShouldBe(OrderStatus.Filled);
        result.Trades.Select(t => (t.Price, t.Quantity)).ShouldBe(new[] { (50L, 1L), (51L, 2L), (52L, 1L) });
        _book.GetSnapshot().Asks.ShouldHaveSingleItem().ShouldBe(new DepthLevel(52, 2, 1));
    }

    [Fact]
    public void Market_order_never_rests_when_liquidity_runs_out()
    {
        _book.Submit(OrderRequest.Limit("alice", Side.Sell, price: 50, quantity: 2));

        var result = _book.Submit(OrderRequest.Market("bob", Side.Buy, quantity: 5));

        result.Status.ShouldBe(OrderStatus.RemainderCancelled);
        result.FilledQuantity.ShouldBe(2);
        result.RestingQuantity.ShouldBe(0);
        _book.BestBid.ShouldBe(null);
        _book.OpenOrderCount.ShouldBe(0);
    }

    [Fact]
    public void Orders_that_do_not_cross_do_not_trade()
    {
        _book.Submit(OrderRequest.Limit("alice", Side.Sell, price: 50, quantity: 1));

        var result = _book.Submit(OrderRequest.Limit("bob", Side.Buy, price: 49, quantity: 1));

        result.Status.ShouldBe(OrderStatus.Resting);
        _book.BestBid.ShouldBe(49);
        _book.BestAsk.ShouldBe(50);
    }

    [Fact]
    public void Cancel_removes_the_order_and_empty_price_levels()
    {
        var placed = _book.Submit(OrderRequest.Limit("alice", Side.Buy, price: 50, quantity: 4));

        _book.Cancel(placed.OrderId, "alice").ShouldBeTrue();

        _book.BestBid.ShouldBe(null);
        _book.OpenOrderCount.ShouldBe(0);
        _book.Cancel(placed.OrderId, "alice").ShouldBeFalse("it is already gone");
    }

    [Fact]
    public void Only_the_owner_can_cancel_an_order()
    {
        var placed = _book.Submit(OrderRequest.Limit("alice", Side.Buy, price: 50, quantity: 4));

        _book.Cancel(placed.OrderId, "mallory").ShouldBeFalse();

        _book.BestBid.ShouldBe(50);
    }

    [Fact]
    public void A_trader_never_trades_with_themselves()
    {
        var resting = _book.Submit(OrderRequest.Limit("alice", Side.Sell, price: 50, quantity: 1));

        var result = _book.Submit(OrderRequest.Limit("alice", Side.Buy, price: 50, quantity: 1));

        result.Trades.ShouldBeEmpty();
        result.SelfTradeCancelledOrderIds.ShouldBe(new[] { resting.OrderId });
        result.Status.ShouldBe(OrderStatus.Resting);
        _book.BestAsk.ShouldBe(null);
        _book.BestBid.ShouldBe(50);
    }

    [Fact]
    public void Snapshot_aggregates_orders_per_price_level()
    {
        _book.Submit(OrderRequest.Limit("a", Side.Buy, price: 50, quantity: 2));
        _book.Submit(OrderRequest.Limit("b", Side.Buy, price: 50, quantity: 3));
        _book.Submit(OrderRequest.Limit("c", Side.Buy, price: 48, quantity: 1));

        var snapshot = _book.GetSnapshot();

        snapshot.Bids.ShouldBe(new[] { new DepthLevel(50, 5, 2), new DepthLevel(48, 1, 1) });
        snapshot.Asks.ShouldBeEmpty();
        snapshot.LastSequence.ShouldBe(_book.LastSequence);
    }

    [Theory]
    [InlineData(OrderType.Limit, 0L, 50L)]
    [InlineData(OrderType.Limit, -5L, 50L)]
    [InlineData(OrderType.Limit, 1_001L, 50L)]
    [InlineData(OrderType.Limit, 10L, 0L)]
    [InlineData(OrderType.Limit, 10L, null)]
    [InlineData(OrderType.Market, 10L, 50L)]
    public void Invalid_orders_are_rejected_without_touching_the_book(OrderType type, long quantity, long? price)
    {
        var result = _book.Submit(new OrderRequest("alice", Side.Buy, type, quantity, price));

        result.Status.ShouldBe(OrderStatus.Rejected);
        result.RejectReason.ShouldNotBeNullOrWhiteSpace();
        _book.OpenOrderCount.ShouldBe(0);
    }
}
