namespace Tradeground.Game.Tests;

public sealed class DeterministicRandomTests
{
    [Fact]
    public void Output_is_pinned_to_the_reference_SplitMix64_values()
    {
        // Golden values from the published SplitMix64 reference implementation with seed 0.
        // If this test ever fails, every saved Daily Challenge and replay would silently change.
        var random = new DeterministicRandom(0);

        random.NextUInt64().ShouldBe(0xE220A8397B1DCDAFUL);
        random.NextUInt64().ShouldBe(0x6E789E6AA1B965F4UL);
    }

    [Fact]
    public void Same_seed_gives_the_same_sequence()
    {
        var a = new DeterministicRandom(12345);
        var b = new DeterministicRandom(12345);

        Enumerable.Range(0, 1_000).Select(_ => a.Next(1, 7))
            .ShouldBe(Enumerable.Range(0, 1_000).Select(_ => b.Next(1, 7)));
    }

    [Fact]
    public void Next_stays_in_range_and_hits_every_value()
    {
        var random = new DeterministicRandom(7);
        var rolls = Enumerable.Range(0, 6_000).Select(_ => random.Next(1, 7)).ToList();

        rolls.ShouldAllBe(r => r >= 1 && r <= 6);
        rolls.Distinct().Order().ShouldBe(new[] { 1, 2, 3, 4, 5, 6 });
    }
}
