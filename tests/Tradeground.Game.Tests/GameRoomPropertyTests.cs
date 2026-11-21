using FsCheck.Xunit;
using Tradeground.Engine;

namespace Tradeground.Game.Tests;

/// <summary>The money rules: for ANY random trading session, nothing is created, lost or over-limit.</summary>
public sealed class GameRoomPropertyTests
{
    private static readonly string[] Players = ["p0", "p1", "p2", "p3"];

    [Property(MaxTest = 200)]
    public void Money_and_contracts_are_conserved_and_limits_hold(int[] ops, ulong seed)
    {
        var mode = TestModes.TraderDice();
        var room = PlayRandomSession(mode, ops, seed, afterEachStep: r =>
        {
            r.Ledger.TotalCash.ShouldBe(mode.StartingCash * Players.Length, "cash only moves between players");
            r.Ledger.TotalPosition.ShouldBe(0, "every long contract has a matching short");
            r.Ledger.Accounts.ShouldAllBe(a => Math.Abs(a.Position) <= mode.MaxPosition);
        });

        room.Settle().Results.Sum(r => r.ProfitAndLoss).ShouldBe(0, "trading is zero-sum");
    }

    private static GameRoom PlayRandomSession(GameMode mode, int[] ops, ulong seed, Action<GameRoom> afterEachStep)
    {
        var room = new GameRoom(mode, seed);
        foreach (var player in Players)
        {
            room.Join(player, player);
        }

        var random = new DeterministicRandom(seed);
        var placed = new List<(string Trader, long OrderId)>();

        foreach (var raw in ops ?? [])
        {
            var trader = Players[random.Next(0, Players.Length)];
            if (raw % 10 == 0 && placed.Count > 0)
            {
                var (owner, orderId) = placed[random.Next(0, placed.Count)];
                room.Cancel(owner, orderId);
            }
            else
            {
                var side = random.Next(0, 2) == 0 ? Side.Buy : Side.Sell;
                var quantity = random.Next(1, (int)mode.MaxOrderQuantity + 1);
                var request = random.Next(0, 8) == 0
                    ? OrderRequest.Market(trader, side, quantity)
                    : OrderRequest.Limit(trader, side, random.Next(20, 51), quantity);

                var result = room.Submit(request);
                if (result.RestingQuantity > 0)
                {
                    placed.Add((trader, result.OrderId));
                }
            }

            afterEachStep(room);
        }

        return room;
    }
}
