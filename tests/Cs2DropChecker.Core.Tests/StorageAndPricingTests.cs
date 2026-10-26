using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Settings;
using Cs2DropChecker.Core.Storage;

namespace Cs2DropChecker.Core.Tests;

public sealed class StorageAndPricingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs2dc-tests-" + Guid.NewGuid().ToString("N"));

    public StorageAndPricingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Database_round_trips_snapshots_drops_values_and_prices()
    {
        var db = new AppDatabase(Path.Combine(_dir, "t.db"));
        var item = ClassifierTests.Skin("42", "AK-47 | Slate");
        db.SaveSnapshot(ClassifierTests.Snap(false, 3, item));
        var snap = db.LoadLatestSnapshot(1);
        Assert.NotNull(snap);
        Assert.Equal("42", Assert.Single(snap!.Items).AssetId);
        Assert.Equal(3, snap.TotalCount);

        var rec = new DropRecord { SteamId = 1, SeenAt = DateTimeOffset.UtcNow, Kind = ArrivalKind.CaseOpened, Item = item, PriceAtDrop = 19.7m, Currency = "EUR", PriceSource = "steam-feed" };
        var id = db.AddDrop(rec);
        Assert.True(id > 0);
        Assert.Equal(0, db.AddDrop(rec));
        db.UpdateDropPrice(id, 20.15m, "EUR", "steam-live");
        var drop = Assert.Single(db.GetDrops(1, 10));
        Assert.Equal(20.15m, drop.PriceAtDrop);
        Assert.Equal("steam-live", drop.PriceSource);
        Assert.Equal(ArrivalKind.CaseOpened, drop.Kind);
        Assert.Equal(item.Float, drop.Item.Float);
        Assert.Equal(1, db.CountDrops(1));

        db.AddValuePoint(1, DateTimeOffset.UtcNow.AddHours(-2), 100m, "EUR", 10, 12);
        db.AddValuePoint(1, DateTimeOffset.UtcNow, 120.5m, "EUR", 11, 12);
        Assert.Equal(2, db.GetValueHistory(1, DateTimeOffset.UtcNow.AddDays(-1), "EUR").Count);
        Assert.Equal(100m, db.GetLastValuePoint(1, "EUR", DateTimeOffset.UtcNow.AddHours(-1))!.Total);

        db.UpsertPrice("AK-47 | Slate (Field-Tested)", "steam-live", "EUR", 19.7m, DateTimeOffset.UtcNow);
        db.UpsertPrice("AK-47 | Slate (Field-Tested)", "steam-live", "EUR", 18.95m, DateTimeOffset.UtcNow);
        Assert.Equal(18.95m, db.GetCachedPrice("AK-47 | Slate (Field-Tested)", "steam-live", "EUR")!.Price);
        db.AddPollLog("public", "Ok", 1, null);
    }

    [Fact]
    public void Settings_round_trip_and_normalise()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var s = store.Load();
        Assert.False(s.FirstRunDone);
        s.SteamId = "76561198000000042";
        s.PollIntervalMinutes = 1;
        s.Currency = "XXX";
        s.PriceSources = [new PriceSourceSetting { Id = "skinport", Enabled = false }, new PriceSourceSetting { Id = "bogus" }];
        s.Overlay.SizePercent = 500;
        store.Save(s);
        var loaded = store.Load();
        Assert.Equal(76561198000000042UL, loaded.SteamId64);
        Assert.Equal(2, loaded.PollIntervalMinutes);
        Assert.Equal("EUR", loaded.Currency);
        Assert.Equal(["skinport", "steam-live", "steam-feed"], loaded.PriceSources.Select(p => p.Id).ToArray());
        Assert.False(loaded.PriceSources[0].Enabled);
        Assert.Equal(160, loaded.Overlay.SizePercent);
    }

    [Fact]
    public void Price_service_respects_order_and_enabled_flags()
    {
        var settings = new AppSettings { Currency = "EUR" };
        var http = new HttpClient();
        var fx = new FxService(http, _dir);
        fx.SetRates(new Dictionary<string, decimal> { ["EUR"] = 0.9m });
        var feed = new CsgoTraderSteamSource(http, fx, _dir);
        feed.SetPrices(new Dictionary<string, decimal> { ["A"] = 10m, ["B"] = 2m });
        var skinport = new SkinportSource(http, _dir);
        skinport.SetPrices("EUR", new Dictionary<string, decimal> { ["A"] = 7m, ["C"] = 3m });
        var live = new SteamLivePriceSource(http);
        var svc = new PriceService(live, feed, skinport, fx, () => settings);

        Assert.Equal(new PriceQuote(9m, "EUR", "steam-feed", default).Price, svc.GetCached("A")!.Price);
        Assert.Equal("skinport", svc.GetCached("C")!.SourceId);
        Assert.Null(svc.GetCached("D"));

        settings.PriceSources = [new() { Id = "skinport" }, new() { Id = "steam-feed" }, new() { Id = "steam-live", Enabled = false }];
        Assert.Equal(7m, svc.GetCached("A")!.Price);
        settings.PriceSources[0].Enabled = false;
        Assert.Equal(9m, svc.GetCached("A")!.Price);
        Assert.Null(svc.GetCached("C"));
    }

    [Fact]
    public void Item_search_matches_every_word_and_ranks_prefix_then_short_names()
    {
        string[] names =
        [
            "StatTrak™ AK-47 | Redline (Field-Tested)",
            "AK-47 | Redline (Minimal Wear)",
            "AK-47 | Redline (Field-Tested)",
            "Souvenir AWP | Redline (Field-Tested)",
            "AK-47 | Slate (Field-Tested)",
        ];
        Assert.Equal(
            ["AK-47 | Redline (Field-Tested)", "AK-47 | Redline (Minimal Wear)", "StatTrak™ AK-47 | Redline (Field-Tested)"],
            ItemSearch.Find(names, "ak-47 redline", 10));
        Assert.Equal(["AK-47 | Redline (Field-Tested)"], ItemSearch.Find(names, "AK-47 | Redline (Field", 1));
        Assert.Empty(ItemSearch.Find(names, "  | ", 10));
        Assert.Empty(ItemSearch.Find(names, "karambit", 10));
    }

    [Fact]
    public void Price_service_searches_both_price_lists_without_duplicates()
    {
        var settings = new AppSettings { Currency = "EUR" };
        var http = new HttpClient();
        var fx = new FxService(http, _dir);
        fx.SetRates(new Dictionary<string, decimal> { ["EUR"] = 0.9m });
        var feed = new CsgoTraderSteamSource(http, fx, _dir);
        feed.SetPrices(new Dictionary<string, decimal> { ["AWP | Asiimov (Field-Tested)"] = 100m, ["Glock-18 | Fade (Factory New)"] = 1000m });
        var skinport = new SkinportSource(http, _dir);
        skinport.SetPrices("EUR", new Dictionary<string, decimal> { ["AWP | Asiimov (Field-Tested)"] = 85m, ["AWP | Asiimov (Battle-Scarred)"] = 60m });
        var svc = new PriceService(new SteamLivePriceSource(http), feed, skinport, fx, () => settings);

        Assert.Equal(["AWP | Asiimov (Field-Tested)", "AWP | Asiimov (Battle-Scarred)"], svc.Search("asiimov awp", 5));
        var onlyOnSkinport = Assert.Single(svc.Search("asiimov battle", 5));
        Assert.Equal(60m, svc.GetCached(onlyOnSkinport)!.Price);
    }
    [Fact]
    public void Feed_parsers_pick_the_right_fields()
    {
        var feed = CsgoTraderSteamSource.Parse("""{"A":{"last_24h":null,"last_7d":1.5},"B":{"last_24h":2.25},"C":{"last_24h":null},"D":5}""");
        Assert.Equal(1.5m, feed["A"]);
        Assert.Equal(2.25m, feed["B"]);
        Assert.False(feed.ContainsKey("C"));
        Assert.False(feed.ContainsKey("D"));

        var sp = SkinportSource.Parse("""[{"market_hash_name":"A","suggested_price":null,"min_price":0.52},{"market_hash_name":"B","suggested_price":26.79,"min_price":20.25}]""");
        Assert.Equal(0.52m, sp["A"]);
        Assert.Equal(26.79m, sp["B"]);

        var fx = FxService.Parse("""{"amount":1.0,"base":"USD","date":"2026-10-23","rates":{"EUR":0.89342,"GBP":0.74804}}""");
        Assert.Equal(0.89342m, fx["EUR"]);
        Assert.Equal(1m, fx["USD"]);
    }
}
