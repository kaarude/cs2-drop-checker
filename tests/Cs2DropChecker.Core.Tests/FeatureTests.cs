using Cs2DropChecker.Core.Alerts;
using Cs2DropChecker.Core.Detection;
using Cs2DropChecker.Core.Friends;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Profit;
using Cs2DropChecker.Core.Selling;
using Cs2DropChecker.Core.Settings;
using Cs2DropChecker.Core.Storage;
using Cs2DropChecker.Core.Weekly;
using Microsoft.Data.Sqlite;

namespace Cs2DropChecker.Core.Tests;

public sealed class ProfitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static CaseOpening Opening(ArrivalKind kind, string? container, decimal? casePrice, decimal? key, decimal? item,
        string currency = "EUR", DateTimeOffset? at = null, string itemName = "P250 | Sand Dune") => new()
    {
        DropId = Random.Shared.NextInt64(1, long.MaxValue),
        OpenedAt = at ?? T0,
        Kind = kind,
        ContainerName = container,
        ContainerHashName = container,
        CasePrice = casePrice,
        KeyPrice = key,
        Currency = currency,
        Item = new InventoryItem { Name = itemName, MarketHashName = itemName },
        ItemPrice = item,
        ItemCurrency = currency,
    };

    [Fact]
    public void Summary_counts_only_complete_openings_in_the_chosen_currency()
    {
        var openings = new[]
        {
            Opening(ArrivalKind.CaseOpened, "Kilowatt Case", 1.00m, 2.30m, 0.50m, itemName: "Bad"),
            Opening(ArrivalKind.CapsuleOpened, "Paris 2023 Legends Sticker Capsule", 1.00m, null, 3.00m, itemName: "Good"),
            Opening(ArrivalKind.CaseOpened, null, null, null, 9.00m) with { Backfilled = true },
            Opening(ArrivalKind.CaseOpened, "Kilowatt Case", 1.00m, 2.30m, 50m, currency: "USD"),
            Opening(ArrivalKind.CaseOpened, "Kilowatt Case", 1.00m, null, 5m),
        };
        var s = ProfitCalculator.Summarize(openings, "EUR");
        Assert.Equal(5, s.Openings);
        Assert.Equal(2, s.Counted);
        Assert.Equal(3, s.Incomplete);
        Assert.Equal(4.30m, s.Spent);
        Assert.Equal(3.50m, s.Returned);
        Assert.Equal(-0.80m, s.Net);
        Assert.Equal(-18.6m, s.RoiPercent);
        Assert.Equal("Good", s.Best!.Item.Name);
        Assert.Equal("Bad", s.Worst!.Item.Name);
        Assert.Equal(2, s.ByContainer.Count);
        var kilowatt = s.ByContainer.Single(g => g.Name == "Kilowatt Case");
        Assert.Equal(-2.80m, kilowatt.Net);
        Assert.Equal(-84.8m, kilowatt.RoiPercent);
        Assert.Equal("You're down €0.80", ProfitCalculator.Headline(s.Net, "EUR"));
    }

    [Fact]
    public void Summary_filters_by_period_and_handles_empty()
    {
        var old = Opening(ArrivalKind.CapsuleOpened, "Capsule", 1m, null, 2m, at: T0.AddDays(-40));
        var recent = Opening(ArrivalKind.CapsuleOpened, "Capsule", 1m, null, 0.25m, at: T0);
        var month = ProfitCalculator.Summarize([old, recent], "EUR", since: T0.AddDays(-30));
        Assert.Equal(1, month.Counted);
        Assert.Equal(-0.75m, month.Net);
        Assert.Equal(-75m, month.RoiPercent);
        Assert.Same(ProfitSummary.Empty, ProfitCalculator.Summarize([], "EUR"));
        Assert.Null(ProfitSummary.Empty.RoiPercent);
        Assert.Equal("You're up £3.20", ProfitCalculator.Headline(3.2m, "GBP"));
        Assert.Equal("You're even", ProfitCalculator.Headline(0, "GBP"));
        Assert.Equal("-€1.50", ProfitCalculator.Signed(-1.5m, "EUR"));
        Assert.Equal("+€1.50", ProfitCalculator.Signed(1.5m, "EUR"));
    }

    [Fact]
    public void Key_price_uses_setting_then_store_price()
    {
        decimal? Rate(string from, string to) => (from, to) switch
        {
            ("USD", "GBP") => 0.75m,
            ("EUR", "GBP") => 0.85m,
            _ => from == to ? 1m : null,
        };
        Assert.Equal(2.15m, KeyPrices.Effective(2.15m, "GBP", "GBP", Rate));
        Assert.Equal(2.13m, KeyPrices.Effective(2.50m, "EUR", "GBP", Rate));
        Assert.Equal(1.87m, KeyPrices.Effective(null, null, "GBP", Rate));
        Assert.Null(KeyPrices.Effective(null, null, "CHF", Rate));
        Assert.True(KeyPrices.NeedsKey(ArrivalKind.CaseOpened));
        Assert.False(KeyPrices.NeedsKey(ArrivalKind.CapsuleOpened));
        Assert.False(KeyPrices.NeedsKey(ArrivalKind.SouvenirPackage));
    }

    [Fact]
    public void Classifier_reports_the_consumed_case()
    {
        var keep = ClassifierTests.Skin("1", "AK-47 | Slate");
        var box = ClassifierTests.Box("2", "Fever Case");
        var prev = ClassifierTests.Snap(false, 2, keep, box);
        var next = ClassifierTests.Snap(false, 2, keep, ClassifierTests.Skin("3", "M4A4 | Choppa"));
        var added = Assert.Single(DropClassifier.Classify(prev, next, true).Added);
        Assert.Equal(ArrivalKind.CaseOpened, added.Kind);
        Assert.Equal("Fever Case", added.Source!.MarketHashName);
    }
}

public sealed class SellTests
{
    [Theory]
    [InlineData("1.15", "1.00")]
    [InlineData("0.03", "0.01")]
    [InlineData("0.02", "0")]
    [InlineData("0.10", "0.08")]
    [InlineData("2.00", "1.75")]
    [InlineData("10.00", "8.70")]
    [InlineData("100.00", "86.97")]
    public void Steam_net_matches_the_market_fee_rules(string buyer, string expected)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(decimal.Parse(expected, inv), SellCalculator.SteamNet(decimal.Parse(buyer, inv)));
    }

    [Fact]
    public void Steam_net_never_exceeds_buyer_price_minus_fees()
    {
        for (var cents = 3; cents < 5000; cents += 7)
        {
            var paid = cents / 100m;
            var net = SellCalculator.SteamNet(paid);
            var r = (long)(net * 100);
            var fees = Math.Max(1, (long)Math.Floor(r * 0.05m)) + Math.Max(1, (long)Math.Floor(r * 0.10m));
            Assert.True(r + fees <= cents, $"paid {paid}");
            var r2 = r + 1;
            var fees2 = Math.Max(1, (long)Math.Floor(r2 * 0.05m)) + Math.Max(1, (long)Math.Floor(r2 * 0.10m));
            Assert.True(r2 + fees2 > cents, $"paid {paid} could receive more");
        }
    }

    [Fact]
    public void Skinport_fee_rounds_up_and_drops_for_high_value_items()
    {
        Assert.Equal(0.80m, SellCalculator.SkinportFee(10m, 1m));
        Assert.Equal(0.09m, SellCalculator.SkinportFee(1.01m, 1m));
        Assert.Equal(0.01m, SellCalculator.SkinportFee(0.05m, 1m));
        Assert.Equal(80m, SellCalculator.SkinportFee(1000m, 1m));
        Assert.Equal(120m, SellCalculator.SkinportFee(2000m, 1m));
        Assert.Equal(66m, SellCalculator.SkinportFee(1100m, 0.95m));
        Assert.Equal(88m, SellCalculator.SkinportFee(1100m, null));
    }

    [Fact]
    public void Compare_picks_the_best_net_and_explains_it()
    {
        var a = SellCalculator.Compare(10.00m, 9.80m, "GBP", 1.15m);
        Assert.Equal(SellVenue.Skinport, a.Best!.Venue);
        Assert.Equal(9.01m, a.Best.Net);
        Assert.Equal(0.31m, a.Margin);
        Assert.Equal("Sell on Skinport, +£0.31 vs Steam", a.Headline);
        var steam = a.Options.Single(o => o.Venue == SellVenue.SteamMarket);
        Assert.Equal(1.30m, steam.Fee);

        var b = SellCalculator.Compare(10.00m, 9.00m, "EUR", 1m);
        Assert.Equal(SellVenue.SteamMarket, b.Best!.Venue);
        Assert.Equal("Sell on Steam Market, +€0.42 vs Skinport", b.Headline);

        var only = SellCalculator.Compare(null, 4m, "EUR", 1m);
        Assert.Equal("Only Skinport has a price", only.Headline);
        Assert.Null(only.Margin);
        Assert.Equal("No price to compare", SellCalculator.Compare(null, null, "EUR", 1m).Headline);
    }

    [Fact]
    public void Fee_constants_match_the_documented_values()
    {
        Assert.Equal(0.05m, SellFees.SteamTransactionFee);
        Assert.Equal(0.10m, SellFees.SteamGameFee);
        Assert.Equal(0.08m, SellFees.SkinportFee);
        Assert.Equal(0.06m, SellFees.SkinportHighValueFee);
        Assert.Equal(1000m, SellFees.SkinportHighValueFromEur);
    }
}

public sealed class AlertTests
{
    private static AlertRule Rule(AlertKind kind, decimal? target = null, decimal? percent = null, decimal? @base = null) =>
        new() { Id = 1, Kind = kind, ItemName = "AK-47 | Slate", Target = target, Percent = percent, Base = @base, Currency = "EUR" };

    /// <summary>Feeds values through the evaluator like successive price refreshes and returns which ones fired.</summary>
    private static bool[] Run(AlertRule rule, decimal?[] values, decimal? reference = null)
    {
        var fired = new bool[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var check = AlertEvaluator.Evaluate(rule, values[i], "EUR", reference);
            fired[i] = check.Fire;
            rule = rule with { Armed = check.Armed };
        }
        return fired;
    }

    [Fact]
    public void Crossing_above_fires_once_until_the_price_drops_back()
    {
        var fired = Run(Rule(AlertKind.ItemAbove, target: 20m), [19m, 20.5m, 21m, 19.8m, 19.5m, 20.1m]);
        Assert.Equal([false, true, false, false, false, true], fired);
        var check = AlertEvaluator.Evaluate(Rule(AlertKind.ItemAbove, target: 20m), 20.5m, "EUR");
        Assert.Equal("AK-47 | Slate is above €20.00", check.Title);
    }

    [Fact]
    public void Crossing_below_mirrors_crossing_above()
    {
        var fired = Run(Rule(AlertKind.InventoryBelow, target: 500m), [520m, 499m, 480m, 505m, 511m, 498m]);
        Assert.Equal([false, true, false, false, false, true], fired);
    }

    [Fact]
    public void Percent_move_fires_either_way_and_resets_at_half()
    {
        var fired = Run(Rule(AlertKind.ItemMove, percent: 10m, @base: 100m), [105m, 111m, 112m, 106m, 104m, 89m]);
        Assert.Equal([false, true, false, false, false, true], fired);
        var down = AlertEvaluator.Evaluate(Rule(AlertKind.ItemMove, percent: 10m, @base: 100m), 89m, "EUR");
        Assert.Equal("AK-47 | Slate is down 11% since the alert was set", down.Title);
    }

    [Fact]
    public void Inventory_24h_move_uses_the_reference_value()
    {
        var rule = Rule(AlertKind.InventoryMove24h, percent: 5m);
        Assert.False(AlertEvaluator.Evaluate(rule, 1040m, "EUR", 1000m).Fire);
        var up = AlertEvaluator.Evaluate(rule, 1060m, "EUR", 1000m);
        Assert.True(up.Fire);
        Assert.Equal("Inventory value is up 6% in 24 h", up.Title);
        Assert.False(AlertEvaluator.Evaluate(rule, 1060m, "EUR", null).Fire);
    }

    [Fact]
    public void Nothing_fires_without_a_price_in_the_same_currency_or_when_disabled()
    {
        var rule = Rule(AlertKind.ItemAbove, target: 1m);
        Assert.False(AlertEvaluator.Evaluate(rule, null, "EUR").Fire);
        Assert.False(AlertEvaluator.Evaluate(rule, 5m, "USD").Fire);
        Assert.False(AlertEvaluator.Evaluate(rule with { Enabled = false }, 5m, "EUR").Fire);
        Assert.True(AlertEvaluator.Evaluate(rule, 5m, "EUR").Fire);
    }

    [Fact]
    public void Discord_webhook_urls_are_validated()
    {
        Assert.True(DiscordWebhook.IsValidUrl("https://discord.com/api/webhooks/123/abc"));
        Assert.True(DiscordWebhook.IsValidUrl(" https://discordapp.com/api/webhooks/123/abc "));
        Assert.False(DiscordWebhook.IsValidUrl("http://discord.com/api/webhooks/123/abc"));
        Assert.False(DiscordWebhook.IsValidUrl("https://evil.example/api/webhooks/123/abc"));
        Assert.False(DiscordWebhook.IsValidUrl("https://discord.com/channels/1/2"));
        Assert.False(DiscordWebhook.IsValidUrl(""));
    }
}

public sealed class WeeklyTests
{
    private static readonly DateTimeOffset Reset = new(2026, 10, 21, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reset_constant_is_wednesday_one_am_utc()
    {
        Assert.Equal(new WeeklyResetTime(DayOfWeek.Wednesday, 1, 0), Cs2Week.Reset);
        Assert.Equal(DayOfWeek.Wednesday, Reset.DayOfWeek);
    }

    [Theory]
    [InlineData("2026-10-21T01:00:00Z", "2026-10-21T01:00:00Z")]
    [InlineData("2026-10-21T00:59:59Z", "2026-10-14T01:00:00Z")]
    [InlineData("2026-10-25T12:00:00Z", "2026-10-21T01:00:00Z")]
    [InlineData("2026-10-28T00:30:00Z", "2026-10-21T01:00:00Z")]
    [InlineData("2026-10-27T20:59:00-04:00", "2026-10-21T01:00:00Z")]
    [InlineData("2026-10-27T21:00:00-04:00", "2026-10-28T01:00:00Z")]
    public void Week_starts_at_the_last_reset(string now, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected), Cs2Week.Start(DateTimeOffset.Parse(now)));
    }

    [Fact]
    public void Weekly_drop_counts_only_after_the_reset()
    {
        var now = Reset.AddDays(4);
        DropRecord Drop(ArrivalKind k, DateTimeOffset at) => new() { Kind = k, SeenAt = at };
        Assert.False(WeeklyReminder.HasWeeklyDrop([Drop(ArrivalKind.WeeklyDrop, Reset.AddMinutes(-1)), Drop(ArrivalKind.CaseOpened, Reset.AddDays(1))], now));
        Assert.True(WeeklyReminder.HasWeeklyDrop([Drop(ArrivalKind.WeeklyDrop, Reset.AddHours(2))], now));
        Assert.Equal(Reset.AddDays(7), Cs2Week.NextReset(now));
    }

    [Fact]
    public void Reminder_time_falls_inside_the_week()
    {
        var utc = TimeZoneInfo.Utc;
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 19, 0, 0, TimeSpan.Zero), WeeklyReminder.ReminderTime(Reset, DayOfWeek.Sunday, new TimeSpan(19, 0, 0), utc));
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 0, 30, 0, TimeSpan.Zero), WeeklyReminder.ReminderTime(Reset, DayOfWeek.Wednesday, new TimeSpan(0, 30, 0), utc));
        var plus2 = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var at = WeeklyReminder.ReminderTime(Reset, DayOfWeek.Wednesday, new TimeSpan(2, 30, 0), plus2);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 0, 30, 0, TimeSpan.Zero).AddDays(7), at.ToUniversalTime());
    }

    [Fact]
    public void Reminder_is_due_once_per_week_without_a_drop()
    {
        var utc = TimeZoneInfo.Utc;
        var time = new TimeSpan(19, 0, 0);
        var due = new DateTimeOffset(2026, 10, 25, 19, 0, 0, TimeSpan.Zero);
        Assert.False(WeeklyReminder.IsDue(due.AddMinutes(-1), false, null, DayOfWeek.Sunday, time, utc));
        Assert.True(WeeklyReminder.IsDue(due, false, null, DayOfWeek.Sunday, time, utc));
        Assert.True(WeeklyReminder.IsDue(due.AddDays(2), false, Reset.AddDays(-7), DayOfWeek.Sunday, time, utc));
        Assert.False(WeeklyReminder.IsDue(due, false, Reset, DayOfWeek.Sunday, time, utc));
        Assert.False(WeeklyReminder.IsDue(due, true, null, DayOfWeek.Sunday, time, utc));
    }

    [Fact]
    public void Reminder_settings_parse_and_normalise_time()
    {
        var s = new AppSettings();
        s.WeeklyReminder.Time = "nonsense";
        s.Friends.RefreshMinutes = 1;
        SettingsStore.Normalize(s);
        Assert.Equal("19:00", s.WeeklyReminder.Time);
        Assert.Equal(15, s.Friends.RefreshMinutes);
        s.WeeklyReminder.Time = "07:45";
        Assert.Equal(new TimeSpan(7, 45, 0), s.WeeklyReminder.TimeOfDay);
    }
}

public sealed class FriendsTests
{
    [Fact]
    public void Friends_page_blocks_are_parsed()
    {
        const string html = """
            <div class="profile_friends search_results" id="search_results">
              <div class="selectable friend_block_v2 persona offline " data-steamid="76561198000000001" data-miniprofile="39734273">
                <a class="selectable_overlay" href="https://steamcommunity.com/id/alpha"></a>
                <div class="player_avatar friend_block_link_overlay offline"><img src="https://avatars.steamstatic.com/a_medium.jpg"></div>
                <div class="friend_block_content">Alpha &amp; Co<br><span class="friend_small_text">Last Online 3 days ago</span></div>
              </div>
              <div class="selectable friend_block_v2 persona in-game " data-steamid="76561198000000002" data-miniprofile="1">
                <div class="player_avatar"><img src="https://avatars.steamstatic.com/b_medium.jpg"></div>
                <div class="friend_block_content">Bravo<br><span class="friend_small_text">Counter-Strike 2</span></div>
              </div>
              <div class="selectable friend_block_v2 persona offline " data-steamid="76561198000000001"></div>
            </div>
            """;
        var friends = FriendsListParser.Parse(html);
        Assert.Equal(2, friends.Count);
        Assert.Equal(new SteamFriend(76561198000000001, "Alpha & Co", "https://avatars.steamstatic.com/a_medium.jpg"), friends[0]);
        Assert.Equal("Bravo", friends[1].Name);
        Assert.Empty(FriendsListParser.Parse("<html>login</html>"));
    }

    [Fact]
    public void Friend_arrivals_infer_openings_from_public_changes()
    {
        var keep = ClassifierTests.Skin("1", "AK-47 | Slate");
        var prev = ClassifierTests.Snap(false, 2, keep, ClassifierTests.Box("2", "Fever Case"));
        var skin = ClassifierTests.Skin("3", "M4A4 | Choppa");
        var next = ClassifierTests.Snap(false, 2, keep, skin);
        PriceQuote? Price(string name) => name switch
        {
            "Fever Case" => new PriceQuote(0.80m, "EUR", "skinport", default),
            "M4A4 | Choppa (Field-Tested)" => new PriceQuote(0.40m, "EUR", "skinport", default),
            _ => null,
        };
        Assert.Empty(FriendTracker.Arrivals(7, null, next, Price, 2.30m, "EUR"));
        var a = Assert.Single(FriendTracker.Arrivals(7, prev, next, Price, 2.30m, "EUR"));
        Assert.Equal(ArrivalKind.CaseOpened, a.Kind);
        Assert.Equal("Fever Case", a.ContainerHashName);
        Assert.Equal(-2.70m, a.CaseNet);
    }

    [Fact]
    public void Leaderboard_ranks_by_week_value_and_puts_private_last()
    {
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var weekStart = Cs2Week.Start(now);
        var drops = new List<DropRecord>
        {
            new() { Kind = ArrivalKind.WeeklyDrop, SeenAt = weekStart.AddHours(1), PriceAtDrop = 3m, Currency = "EUR", Item = new() { Name = "Case A" } },
            new() { Kind = ArrivalKind.CaseOpened, SeenAt = weekStart.AddHours(2), PriceAtDrop = 12m, Currency = "EUR", Item = new() { Name = "Knife" } },
            new() { Kind = ArrivalKind.WeeklyDrop, SeenAt = weekStart.AddHours(-2), PriceAtDrop = 99m, Currency = "EUR", Item = new() { Name = "Old" } },
            new() { Kind = ArrivalKind.Moved, SeenAt = weekStart.AddHours(3), PriceAtDrop = 50m, Currency = "EUR", Item = new() { Name = "Moved" } },
            new() { Kind = ArrivalKind.Purchase, SeenAt = weekStart.AddHours(4), PriceAtDrop = 80m, Currency = "EUR", Item = new() { Name = "Bought" } },
        };
        var you = Leaderboard.ForYou(1, "Me", "", drops, 400m, ProfitSummary.Empty, "EUR", now);
        Assert.Equal("Knife", you.BestName);
        Assert.Equal(15m, you.WeekValue);
        Assert.Equal(2, you.WeekCount);
        Assert.Null(you.CaseNet);

        var friend = new Friend { SteamId = 2, Name = "Pal", Status = FriendStatus.Ok, Value = 900m, Currency = "EUR" };
        var arrivals = new[]
        {
            new FriendArrival { FriendId = 2, SeenAt = weekStart.AddHours(5), Kind = ArrivalKind.CaseOpened, Price = 20m, Currency = "EUR", CasePrice = 1m, KeyPrice = 2m, Item = new() { Name = "Glock" } },
            new FriendArrival { FriendId = 2, SeenAt = weekStart.AddHours(6), Kind = ArrivalKind.LatePurchase, Price = 300m, Currency = "EUR", Item = new() { Name = "Bought" } },
        };
        var pal = Leaderboard.ForFriend(friend, arrivals, "EUR", now);
        Assert.Equal(20m, pal.WeekValue);
        Assert.Equal(17m, pal.CaseNet);
        Assert.True(pal.CaseNetEstimated);
        var hidden = Leaderboard.ForFriend(new Friend { SteamId = 3, Name = "Shy", Status = FriendStatus.Private }, [], "EUR", now);
        var ranked = Leaderboard.Rank([hidden, you, pal]);
        Assert.Equal(["Pal", "Me", "Shy"], ranked.Select(r => r.Name).ToArray());
    }
}

public sealed class FeatureStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs2dc-tests-" + Guid.NewGuid().ToString("N"));

    public FeatureStorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static void Exec(string file, string sql)
    {
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Version_1_database_is_backed_up_and_migrated_without_losing_history()
    {
        var file = Path.Combine(_dir, "data.db");
        var db = new AppDatabase(file);
        var item = ClassifierTests.Skin("42", "AK-47 | Slate");
        var openId = db.AddDrop(new DropRecord { SteamId = 1, SeenAt = DateTimeOffset.UtcNow, Kind = ArrivalKind.CaseOpened, Item = item, PriceAtDrop = 4.5m, Currency = "EUR" });
        db.AddDrop(new DropRecord { SteamId = 1, SeenAt = DateTimeOffset.UtcNow, Kind = ArrivalKind.WeeklyDrop, Item = ClassifierTests.Skin("43", "P90 | Sand") });
        db.AddValuePoint(1, DateTimeOffset.UtcNow, 100m, "EUR", 1, 1);
        SqliteConnection.ClearAllPools();

        // Turn it back into a version 1 database as shipped in 0.1.0.
        Exec(file, """
            DROP TABLE case_opening; DROP TABLE friend; DROP TABLE friend_snapshot; DROP TABLE friend_arrival; DROP TABLE price_alert;
            UPDATE schema_version SET version=1;
            """);

        var migrated = new AppDatabase(file);
        Assert.Equal(AppDatabase.LatestSchemaVersion, migrated.CurrentSchemaVersion);
        Assert.True(File.Exists(file + ".v1.bak"));
        Assert.Equal(2, migrated.CountDrops(1));
        Assert.Single(migrated.GetValueHistory(1, DateTimeOffset.UtcNow.AddDays(-1), "EUR"));
        var opening = Assert.Single(migrated.GetOpenings(1));
        Assert.Equal(openId, opening.DropId);
        Assert.True(opening.Backfilled);
        Assert.False(opening.ContainerKnown);
        Assert.Equal(4.5m, opening.ItemPrice);

        migrated.UpdateOpeningCost(openId, "Fever Case", "Fever Case", 0.8m, 2.3m, "EUR");
        var fixedUp = Assert.Single(migrated.GetOpenings(1));
        Assert.True(fixedUp.IsComplete("EUR"));
        Assert.Equal(1.4m, fixedUp.Net);

        SqliteConnection.ClearAllPools();
        var again = new AppDatabase(file);
        Assert.Single(again.GetOpenings(1));
    }

    [Fact]
    public void New_database_starts_at_the_latest_version_without_backup()
    {
        var file = Path.Combine(_dir, "fresh.db");
        var db = new AppDatabase(file);
        Assert.Equal(AppDatabase.LatestSchemaVersion, db.CurrentSchemaVersion);
        Assert.False(File.Exists(file + ".v1.bak"));
    }

    [Fact]
    public void Friends_and_alerts_round_trip()
    {
        var db = new AppDatabase(Path.Combine(_dir, "f.db"));
        db.UpsertFriend(new Friend { SteamId = 76561198000000001, Name = "Alpha", AvatarUrl = "a", Source = FriendSource.Manual, AddedAt = DateTimeOffset.UtcNow });
        db.UpsertFriend(new Friend { SteamId = 76561198000000001, Name = "Alpha 2", AvatarUrl = "a", Source = FriendSource.Steam, AddedAt = DateTimeOffset.UtcNow });
        var f = Assert.Single(db.GetFriends());
        Assert.Equal("Alpha 2", f.Name);
        Assert.Equal(FriendSource.Manual, f.Source);
        db.UpdateFriendStatus(f.SteamId, FriendStatus.Private, DateTimeOffset.UtcNow, null, null, null);
        Assert.Equal(FriendStatus.Private, db.GetFriends()[0].Status);
        db.SaveFriendSnapshot(new InventorySnapshot { SteamId = f.SteamId, TakenAt = DateTimeOffset.UtcNow, Items = [ClassifierTests.Skin("9", "MP9 | Sand")] });
        Assert.Single(db.LoadFriendSnapshot(f.SteamId)!.Items);
        db.AddFriendArrival(new FriendArrival { FriendId = f.SteamId, SeenAt = DateTimeOffset.UtcNow, Kind = ArrivalKind.NewItem, Item = ClassifierTests.Skin("10", "MP9 | Sand"), Price = 1m, Currency = "EUR" });
        Assert.Single(db.GetFriendArrivals(f.SteamId));
        db.RemoveFriend(f.SteamId);
        Assert.Empty(db.GetFriends());
        Assert.Empty(db.GetFriendArrivals(f.SteamId));
        Assert.Null(db.LoadFriendSnapshot(f.SteamId));

        var id = db.AddAlert(new AlertRule { Kind = AlertKind.ItemMove, MarketHashName = "X", ItemName = "X", Percent = 10m, Base = 5m, Currency = "EUR" });
        db.UpdateAlertState(id, false, DateTimeOffset.UtcNow);
        var a = Assert.Single(db.GetAlerts());
        Assert.False(a.Armed);
        Assert.NotNull(a.FiredAt);
        db.ResetAlert(id, 6m);
        a = db.GetAlerts()[0];
        Assert.True(a.Armed);
        Assert.Equal(6m, a.Base);
        db.SetAlertEnabled(id, false);
        Assert.False(db.GetAlerts()[0].Enabled);
        db.DeleteAlert(id);
        Assert.Empty(db.GetAlerts());
    }
}
