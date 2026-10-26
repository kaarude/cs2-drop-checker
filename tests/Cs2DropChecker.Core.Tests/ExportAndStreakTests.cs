using Cs2DropChecker.Core.Export;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Weekly;

namespace Cs2DropChecker.Core.Tests;

public sealed class DropsCsvTests
{
    [Theory]
    [InlineData("AK-47 | Slate", "AK-47 | Slate")]
    [InlineData("", "")]
    [InlineData("Sticker, Holo", "\"Sticker, Holo\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("@SUM", "'@SUM")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("-1.50", "-1.50")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    public void Fields_are_escaped(string input, string expected) => Assert.Equal(expected, DropsCsv.Escape(input));

    [Fact]
    public void Rows_have_one_cell_per_header_and_use_invariant_numbers()
    {
        var item = ClassifierTests.Skin("42", "StatTrak™ AK-47 | Slate", "Rarity_Mythical_Weapon", wear: 0.0934) with
        {
            RarityName = "Restricted",
            QualityTag = "strange",
            StickerCount = 2,
        };
        var drops = new[]
        {
            new DropRecord
            {
                SeenAt = new DateTimeOffset(2026, 10, 21, 3, 4, 5, TimeSpan.Zero),
                Kind = ArrivalKind.WeeklyDrop,
                Item = item,
                PriceAtDrop = 1234.5m,
                Currency = "EUR",
                PriceSource = PriceSourceIds.Skinport,
            },
            new DropRecord { SeenAt = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), Kind = ArrivalKind.CaseOpened, Item = ClassifierTests.Box("7", "Fever Case") },
        };
        var csv = DropsCsv.Write(drops, name => name == item.MarketHashName ? new PriceQuote(1300m, "EUR", PriceSourceIds.SteamFeed, default) : null);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(string.Join(',', DropsCsv.Header), lines[0]);
        var first = lines[1].Split(',');
        Assert.Equal(DropsCsv.Header.Length, first.Length);
        Assert.Equal("2026-10-21 03:04:05", first[0]);
        Assert.Equal("Weekly drop", first[1]);
        Assert.Equal("Restricted", first[4]);
        Assert.Equal("0.093400", first[6]);
        Assert.Equal("yes", first[8]);
        Assert.Equal("2", first[10]);
        Assert.Equal("1234.50", first[11]);
        Assert.Equal("EUR", first[12]);
        Assert.Equal("Skinport", first[13]);
        Assert.Equal("1300.00", first[14]);
        var second = lines[2].Split(',');
        Assert.Equal(DropsCsv.Header.Length, second.Length);
        Assert.Equal("Case opened", second[1]);
        Assert.Equal("", second[11]);
        Assert.Equal("", second[12]);
    }

    [Fact]
    public void File_name_has_the_date() =>
        Assert.Matches(@"^cs2-drops-\d{4}-\d{2}-\d{2}\.csv$", DropsCsv.FileName(DateTimeOffset.UtcNow));
}

public sealed class WeeklyStreakTests
{
    // Wednesday 2026-10-21 01:00 UTC is a weekly reset.
    private static readonly DateTimeOffset Reset = new(2026, 10, 21, 1, 0, 0, TimeSpan.Zero);

    private static DropRecord Drop(DateTimeOffset at, ArrivalKind kind = ArrivalKind.WeeklyDrop) => new() { SeenAt = at, Kind = kind };

    [Fact]
    public void No_drops_means_no_streak()
    {
        var s = WeeklyStreak.Compute([], Reset.AddDays(2));
        Assert.False(s.ThisWeek);
        Assert.Equal(0, s.Streak);
        Assert.False(s.AtRisk);
        Assert.Equal(Reset.AddDays(7), s.NextReset);
    }

    [Fact]
    public void Consecutive_weeks_count_including_this_one()
    {
        var drops = new[] { Drop(Reset.AddHours(5)), Drop(Reset.AddDays(-3)), Drop(Reset.AddDays(-10)), Drop(Reset.AddDays(-30)) };
        var s = WeeklyStreak.Compute(drops, Reset.AddDays(1));
        Assert.True(s.ThisWeek);
        Assert.Equal(3, s.Streak);
        Assert.False(s.AtRisk);
    }

    [Fact]
    public void Streak_is_at_risk_until_this_weeks_drop_arrives()
    {
        var drops = new[] { Drop(Reset.AddDays(-1)), Drop(Reset.AddDays(-8)) };
        var s = WeeklyStreak.Compute(drops, Reset.AddDays(3));
        Assert.False(s.ThisWeek);
        Assert.Equal(2, s.Streak);
        Assert.True(s.AtRisk);
    }

    [Fact]
    public void A_missed_week_ends_the_streak_and_only_weekly_drops_count()
    {
        var drops = new[] { Drop(Reset.AddDays(-9)), Drop(Reset.AddDays(1), ArrivalKind.CaseOpened), Drop(Reset.AddDays(-2), ArrivalKind.Purchase) };
        var s = WeeklyStreak.Compute(drops, Reset.AddDays(2));
        Assert.False(s.ThisWeek);
        Assert.Equal(0, s.Streak);
        Assert.False(s.AtRisk);
    }

    [Fact]
    public void Drop_right_at_the_reset_belongs_to_the_new_week()
    {
        var s = WeeklyStreak.Compute([Drop(Reset)], Reset.AddMinutes(1));
        Assert.True(s.ThisWeek);
        Assert.Equal(1, s.Streak);
    }
}
