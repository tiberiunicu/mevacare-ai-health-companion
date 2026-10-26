using MevaCare.AI.HealthCompanion.Modules.Assistant.Features;

namespace MevaCare.AI.HealthCompanion.UnitTests.Insights;

public class NumberGroundingTests
{
    private const string Evidence = """
        {"period":{"from":"2026-10-01","to":"2026-10-30","days":30},
         "trends":[{"metric":"weight_kg","changePerWeek":-0.43,"average":81.234,"points":14},
                   {"metric":"sleep_minutes","average":414,"provider":"whoop"}],
         "insights":[{"statement":"Your average calories on logged days is 1950 kcal, 7.5% below your target of 2100 kcal (12 logged days).","facts":[{"key":"deviation_percent","number":-7.5}]}]}
        """;

    [Fact]
    public void Numbers_that_exist_in_the_data_are_grounded()
    {
        const string text = "Your weight is falling by 0.43 kg per week. You averaged 1950 kcal, 7.5% below the 2100 kcal target, over 12 logged days.";

        Assert.Empty(NumberGrounding.Ungrounded(text, Evidence));
    }

    [Fact]
    public void An_invented_number_is_reported_exactly_as_written()
    {
        const string text = "You lost 2.9 kg and ate 1,234 kcal on average, which is 17% less.";

        Assert.Equal(["2.9", "1,234", "17"], NumberGrounding.Ungrounded(text, Evidence));
    }

    [Theory]
    [InlineData("about 0.4 kg per week")] // rounded
    [InlineData("roughly 81 kg")] // rounded
    [InlineData("an average of 6.9 hours of sleep")] // 414 minutes written as hours
    [InlineData("7 hours")] // 414/60 rounded to whole hours (and a small number anyway)
    [InlineData("a drop of 0,43 kg")] // decimal comma
    public void Roundings_unit_conversions_and_decimal_commas_are_understood(string text) =>
        Assert.Empty(NumberGrounding.Ungrounded(text, Evidence));

    [Fact]
    public void A_number_split_by_a_space_is_judged_part_by_part()
    {
        Assert.Equal(["950"], NumberGrounding.Ungrounded("1 950 kcal", Evidence));
    }

    [Fact]
    public void Dates_from_the_period_are_grounded_and_other_dates_are_not()
    {
        Assert.Empty(NumberGrounding.Ungrounded("From 2026-10-01 to 2026-10-30, so 30 days.", Evidence));
        Assert.Equal(["17"], NumberGrounding.Ungrounded("Around 2026-10-17 something changed.", Evidence)); // judged part by part: 2026 and 10 exist, 17 does not
    }

    [Fact]
    public void Small_whole_numbers_are_let_through_because_they_are_counts_not_claims()
    {
        Assert.Empty(NumberGrounding.Ungrounded("Over the last 2 weeks you had 3 good nights and a 1-day dip, plus 10 steps in the plan.", Evidence));
    }

    [Fact]
    public void A_negative_sign_does_not_matter_but_the_magnitude_does()
    {
        Assert.Empty(NumberGrounding.Ungrounded("a change of -0.43 kg", Evidence));
        Assert.Equal(["0.9"], NumberGrounding.Ungrounded("a change of -0.9 kg", Evidence));
    }

    [Fact]
    public void Each_ungrounded_number_is_listed_once()
    {
        Assert.Equal(["55"], NumberGrounding.Ungrounded("55 here, 55 there, and 55 again", Evidence));
    }

    [Fact]
    public void Text_without_numbers_has_nothing_to_report()
    {
        Assert.Empty(NumberGrounding.Ungrounded("Your sleep looks steadier this month. This is wellness information, not medical advice.", Evidence));
    }
}
