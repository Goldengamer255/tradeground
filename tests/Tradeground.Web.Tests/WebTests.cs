using Tradeground.Contracts;
using Tradeground.Web.Services;

namespace Tradeground.Web.Tests;

public sealed class FormatTests
{
    [Theory]
    [InlineData("1m", 20, "20 minutes")]
    [InlineData("15m", 20, "5 hours")]
    [InlineData("30m", 20, "10 hours")]
    [InlineData("1h", 20, "20 hours")]
    [InlineData("4h", 20, "3 days and 8 hours")]
    [InlineData("1h", 1, "1 hour")]
    [InlineData("4h", 12, "2 days")]
    public void Durations_read_like_a_person_would_say_them(string timeframe, int candles, string expected)
    {
        Format.Duration(timeframe, candles).ShouldBe(expected);
    }

    [Theory]
    [InlineData(12345, "12,345")]
    [InlineData(-5, "−5")]
    [InlineData(34.5, "34.5")]
    [InlineData(0, "0")]
    public void Numbers_use_a_real_minus_sign_and_thousands_separators(decimal value, string expected)
    {
        Format.Number(value).ShouldBe(expected);
    }

    [Fact]
    public void Signed_numbers_always_show_their_sign()
    {
        (Format.Signed(3), Format.Signed(-3), Format.Signed(0)).ShouldBe(("+3", "−3", "0"));
    }

    [Fact]
    public void Percentages_and_chart_index_values()
    {
        Format.Percent(1.236m).ShouldBe("+1.24%");
        Format.Percent(-0.5m).ShouldBe("−0.50%");
        Format.PercentSize(-1.236m).ShouldBe("1.24%");
        Format.Index(10_000).ShouldBe("100.0", "a rebased 10000 is shown as 100.0");
    }

    [Fact]
    public void Times_are_always_shown_in_utc()
    {
        Format.When(new DateTimeOffset(2024, 3, 12, 4, 0, 0, TimeSpan.Zero)).ShouldBe("12 Mar 2024, 04:00 UTC");
        Format.When(new DateTimeOffset(2024, 3, 12, 17, 0, 0, TimeSpan.FromHours(13))).ShouldBe("12 Mar 2024, 04:00 UTC");
    }
}

public sealed class FairValueTests
{
    private static readonly ModeSummary TenDice = new(
        "trader-dice", "Ten Dice", Audience.Trader, "", "Ten Dice Contract", "DICE", "contracts",
        10_000, 50, 20, 300, DiceCount: 10, DiceSides: 6, ValuePerPip: 1);

    [Fact]
    public void With_nothing_known_fair_value_is_the_average_of_every_die()
    {
        var estimate = FairValue.Of(TenDice, new Dictionary<int, int>());

        estimate.ShouldBe(new Estimate(35m, 10, 60, 0));
    }

    [Fact]
    public void Each_known_die_replaces_an_average_with_a_fact()
    {
        var estimate = FairValue.Of(TenDice, new Dictionary<int, int> { [3] = 6, [7] = 1 });

        estimate.Value.ShouldBe(6 + 1 + (8 * 3.5m));
        (estimate.Low, estimate.High).ShouldBe((15L, 55L));
    }

    [Fact]
    public void Known_dice_combine_the_news_and_your_own_die()
    {
        var known = FairValue.Known([new ClueView(2, 5, "News"), new ClueView(9, 1, "News")], new ClueView(4, 3, "Mine"));

        known.ShouldBe(new Dictionary<int, int> { [2] = 5, [9] = 1, [4] = 3 });
    }
}

public sealed class BrowserSafetyTests
{
    /// <summary>
    /// Everything the browser app references is downloaded to the player's machine. The game assembly
    /// holds the chart data (including the future candles) and the hidden dice, so the browser app must
    /// never reference it, or anyone could open the download and read the answers.
    /// </summary>
    [Fact]
    public void The_browser_app_never_ships_the_game_logic()
    {
        var referenced = typeof(Format).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        referenced.ShouldContain("Tradeground.Contracts");
        referenced.ShouldNotContain("Tradeground.Game");
        referenced.ShouldNotContain("Tradeground.Api");
    }
}
