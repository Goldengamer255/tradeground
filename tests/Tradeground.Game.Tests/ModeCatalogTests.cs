namespace Tradeground.Game.Tests;

public sealed class ModeCatalogTests
{
    [Fact]
    public void Built_in_modes_load_and_are_valid()
    {
        var catalog = ModeCatalog.LoadBuiltIn();

        catalog.All.Select(m => m.Id).Order().ShouldBe(new[] { "explorer-lemonade", "trader-dice" });
        catalog.All.ShouldAllBe(m => m.Validate().Count == 0);
        catalog.TryGet("TRADER-DICE", out var mode).ShouldBeTrue("ids are case-insensitive");
        mode!.Audience.ShouldBe(Audience.Trader);
    }

    [Fact]
    public void Invalid_modes_fail_fast_at_startup()
    {
        var broken = TestModes.TraderDice() with { MaxPosition = 0 };

        Should.Throw<InvalidOperationException>(() => new ModeCatalog([broken]))
            .Message.ShouldContain("MaxPosition");
    }

    [Fact]
    public void Duplicate_mode_ids_are_rejected()
    {
        Should.Throw<InvalidOperationException>(() => new ModeCatalog([TestModes.TraderDice(), TestModes.TraderDice()]));
    }
}

internal static class TestModes
{
    public static GameMode TraderDice() => ModeCatalog.LoadBuiltIn().TryGet("trader-dice", out var mode)
        ? mode
        : throw new InvalidOperationException("trader-dice mode missing");
}
