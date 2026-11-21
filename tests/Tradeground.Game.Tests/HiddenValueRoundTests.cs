namespace Tradeground.Game.Tests;

public sealed class HiddenValueRoundTests
{
    private static readonly HiddenValueSpec TenDice = new(10, 6, 1, "Die #{index} rolled a {value}.", "News: die #{index} rolled a {value}.");

    [Fact]
    public void Same_seed_produces_the_same_round()
    {
        var a = new HiddenValueRound(TenDice, seed: 99);
        var b = new HiddenValueRound(TenDice, seed: 99);

        a.TrueValue.ShouldBe(b.TrueValue);
        a.NextPrivateClue().ShouldBe(b.NextPrivateClue());
        a.RevealNextPublicClue().ShouldBe(b.RevealNextPublicClue());
    }

    [Fact]
    public void Expected_value_with_no_information_is_dice_count_times_average_roll()
    {
        new HiddenValueRound(TenDice, seed: 1).ExpectedValueWithNoInformation.ShouldBe(35m);
    }

    [Fact]
    public void Revealing_every_public_clue_reveals_the_true_value()
    {
        var round = new HiddenValueRound(TenDice, seed: 2024);

        while (round.RevealNextPublicClue() is not null)
        {
        }

        round.PublicClues.Count.ShouldBe(10);
        round.PublicClues.Select(c => c.DieIndex).Distinct().Count().ShouldBe(10, "each die is revealed exactly once");
        round.PublicClues.Sum(c => c.Value).ShouldBe((int)round.TrueValue);
        round.HasMorePublicClues.ShouldBeFalse();
    }

    [Fact]
    public void Clue_text_fills_in_the_template()
    {
        var clue = new HiddenValueRound(TenDice, seed: 5).NextPrivateClue();

        clue.Text.ShouldBe($"Die #{clue.DieIndex + 1} rolled a {clue.Value}.");
    }

    [Fact]
    public void Private_clues_cycle_through_every_die_before_repeating()
    {
        var round = new HiddenValueRound(TenDice, seed: 8);

        var first = Enumerable.Range(0, 10).Select(_ => round.NextPrivateClue().DieIndex).ToList();

        first.Distinct().Count().ShouldBe(10);
        round.NextPrivateClue().DieIndex.ShouldBe(first[0]);
    }
}
