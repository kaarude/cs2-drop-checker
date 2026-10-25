using Cs2DropChecker.Core.Detection;
using Cs2DropChecker.Core.Market;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Steam;
using Cs2DropChecker.Core.Storage;
using Cs2DropChecker.Core.Updates;

namespace Cs2DropChecker.Core.Tests;

public sealed class MarketTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs2dc-market-" + Guid.NewGuid().ToString("N"));
    private static readonly WalletInfo Eur = new() { CurrencyCode = 3 };

    public MarketTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static InventoryItem Sellable(string asset, string name) => ClassifierTests.Skin(asset, name) with { Marketable = true };

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    [InlineData(10, 12)]
    [InlineData(19, 21)]
    [InlineData(20, 23)]
    [InlineData(100, 115)]
    [InlineData(101, 116)]
    [InlineData(1000, 1150)]
    public void Fee_for_receive_matches_steam(long receive, long buyer)
    {
        var f = MarketFees.ForReceive(receive, Eur);
        Assert.Equal(buyer, f.BuyerPays);
        Assert.Equal(buyer, f.Receive + f.SteamFee + f.PublisherFee);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(115, 100)]
    [InlineData(116, 101)]
    [InlineData(117, 102)]
    [InlineData(22, 19)]
    [InlineData(1150, 1000)]
    [InlineData(46, 40)]
    public void Receive_for_buyer_price_matches_steam(long buyer, long receive)
    {
        var f = MarketFees.ForBuyerPrice(buyer, Eur);
        Assert.Equal(receive, f.Receive);
        Assert.Equal(buyer, f.BuyerPays);
        Assert.True(MarketFees.ForReceive(f.Receive, Eur).BuyerPays <= buyer);
    }

    [Fact]
    public void Tiny_buyer_price_is_not_a_valid_listing()
    {
        Assert.False(MarketFees.ForBuyerPrice(2, Eur).IsValid);
        Assert.True(MarketFees.ForBuyerPrice(3, Eur).IsValid);
    }

    [Fact]
    public void Wallet_session_and_listings_parse_from_market_home()
    {
        const string html = """
            <script>
            var g_rgWalletInfo = {"wallet_currency":3,"wallet_country":"DE","wallet_state":"","wallet_fee":"1","wallet_fee_minimum":"1","wallet_fee_percent":"0.05","wallet_publisher_fee_percent_default":"0.10","wallet_fee_base":"0","wallet_balance":"1234","wallet_delayed_balance":"0","wallet_max_balance":"200000","wallet_trade_max_balance":"180000","success":1,"rwgrsn":-2};
            g_sessionID = "abc123def";
            g_steamID = "76561198000000001";
            </script>
            <div class="my_listing_section market_content_block market_home_listing_table">
              <h3 class="my_market_header"><span class="my_market_header_active">My sell listings</span></h3>
              <div class="market_listing_row market_recent_listing_row listing_111" id="mylisting_111">
                <span class="market_listing_price"><span style="display: inline-block">
                  <span title="This is the price the buyer pays.">
                    1,23€					</span>
                  <br>
                  <span title="This is how much you will receive." style="color: #AFAFAF">
                    (1,07€)					</span>
                </span></span>
                <div class="market_listing_right_cell market_listing_listed_date can_combine">
                  9 Mar	</div>
                <a href="javascript:RemoveMarketListing('mylisting', '111', 730, '2', '9001')" class="item_market_action_button">Remove</a>
                <span id="mylisting_111_name" class="market_listing_item_name" style="color: #D2D2D2;"><a class="market_listing_item_name_link" href="https://steamcommunity.com/market/listings/730/Fracture%20Case">Fracture Case</a></span>
              </div>
            </div>
            <div class="my_listing_section market_content_block market_home_listing_table">
              <h3 class="my_market_header"><span class="my_market_header_active">My listings awaiting confirmation</span></h3>
              <div class="market_listing_row market_recent_listing_row listing_222" id="mylisting_222">
                <span title="This is the price the buyer pays.">1,15€</span>
                <a href="javascript:CancelMarketListingConfirmation('mylisting', '222', 730, '2', '9002')">Cancel</a>
                <span id="mylisting_222_name" class="market_listing_item_name"><a href="https://steamcommunity.com/market/listings/730/AK-47%20%7C%20Slate%20%28Field-Tested%29">AK-47 | Slate (Field-Tested)</a></span>
              </div>
            </div>
            <div class="my_listing_section"><h3 class="my_market_header"><span class="my_market_header_active">My buy orders</span></h3>
              <div class="market_listing_row" id="mybuyorder_333"></div>
            </div>
            """;
        var home = MarketParsers.ParseHome(html);
        Assert.NotNull(home.Wallet);
        Assert.Equal(3, home.Wallet!.CurrencyCode);
        Assert.Equal("EUR", home.Wallet.CurrencyIso);
        Assert.Equal(1234, home.Wallet.Balance);
        Assert.Equal(0.05m, home.Wallet.FeePercent);
        Assert.Equal("abc123def", home.SessionId);
        Assert.Equal(76561198000000001UL, home.SteamId);
        Assert.Equal(2, home.Listings.Count);

        var a = home.Listings[0];
        Assert.Equal(("111", "9001", "2"), (a.ListingId, a.AssetId, a.ContextId));
        Assert.Equal(123, a.BuyerPays);
        Assert.Equal(107, a.Receive);
        Assert.Equal("Fracture Case", a.MarketHashName);
        Assert.Equal("9 Mar", a.ListedOn);
        Assert.False(a.AwaitingConfirmation);

        var b = home.Listings[1];
        Assert.Equal("9002", b.AssetId);
        Assert.True(b.AwaitingConfirmation);
        Assert.Equal(115, b.BuyerPays);
        Assert.Equal(100, b.Receive);
        Assert.Equal("AK-47 | Slate (Field-Tested)", b.MarketHashName);
        Assert.Equal(1, home.ActiveCount);
    }

    [Fact]
    public void Listings_render_json_uses_hovers_and_assets()
    {
        const string json = """
            {"success":true,"pagesize":100,"total_count":1,"start":0,"num_active_listings":1,
             "assets":{"730":{"2":{"9001":{"id":"9001","market_hash_name":"Fracture Case","name":"Fracture Case"}}}},
             "hovers":"CreateItemHoverFromContainer( g_rgAssets, 'mylisting_111_name', 730, '2', '9001', 0 );",
             "results_html":"<div class=\"market_listing_row\" id=\"mylisting_111\"><span title=\"This is the price the buyer pays.\">$0.46</span><span class=\"market_listing_item_name\">Fracture Case</span></div>"}
            """;
        var page = MarketParsers.ParseListingsPage(json, Eur);
        Assert.True(page.Success);
        var l = Assert.Single(page.Listings);
        Assert.Equal("9001", l.AssetId);
        Assert.Equal("Fracture Case", l.MarketHashName);
        Assert.Equal(46, l.BuyerPays);
        Assert.Equal(40, l.Receive);
        Assert.False(MarketParsers.ParseListingsPage("{\"success\":false}", Eur).Success);
    }

    [Fact]
    public void Order_book_parses_with_either_envelope()
    {
        const string inner = """{"amtMaxBuyOrder":43,"amtMinSellOrder":46,"eCurrency":3,"cBuyOrders":3292445,"cSellOrders":165012,"rgCompactBuyOrders":[43,1,41,183],"rgCompactSellOrders":[46,2954,47,4246]}""";
        foreach (var json in new[] { "{\"data\":{\"success\":true,\"data\":" + inner + "}}", "{\"success\":true,\"data\":" + inner + "}" })
        {
            var ob = MarketParsers.ParseOrderBook(json);
            Assert.NotNull(ob);
            Assert.Equal(46, ob!.LowestSell);
            Assert.Equal(43, ob.HighestBuy);
            Assert.Equal(3, ob.CurrencyCode);
            Assert.Equal([new OrderLevel(46, 2954), new OrderLevel(47, 4246)], ob.Sells);
            Assert.Equal(165012, ob.SellCount);
        }
        Assert.Null(MarketParsers.ParseOrderBook("""{"success":false}"""));
        Assert.Null(MarketParsers.ParseOrderBook("[]"));
    }

    [Fact]
    public void Price_history_parses_and_gives_weighted_median()
    {
        const string json = """
            {"success":true,"price_prefix":"","price_suffix":"€","prices":[
              ["Mar 01 2026 01: +0",0.50,"10"],["Mar 09 2026 20: +0",0.40,"1"],["Mar 09 2026 21: +0",0.45,"5"],["Mar 09 2026 22: +0",0.60,"2"]]}
            """;
        var points = MarketParsers.ParsePriceHistory(json);
        Assert.NotNull(points);
        Assert.Equal(4, points!.Count);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 21, 0, 0, TimeSpan.Zero), points[2].At);
        Assert.Equal(5, points[2].Volume);
        Assert.Equal(0.45m, MarketParsers.Median(points, new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero)));
        Assert.Null(MarketParsers.ParsePriceHistory("[]"));
        Assert.Null(MarketParsers.ParsePriceHistory("""{"success":false}"""));
    }

    [Fact]
    public void Sell_response_explains_confirmation_and_errors()
    {
        var ok = MarketParsers.ParseSellResponse(200, """{"success":true,"requires_confirmation":1,"needs_mobile_confirmation":true,"needs_email_confirmation":false,"email_domain":"gmail.com"}""");
        Assert.True(ok.Success);
        Assert.True(ok.NeedsMobileConfirmation);
        Assert.Contains("Steam Mobile", ok.Message);

        var mail = MarketParsers.ParseSellResponse(200, """{"success":true,"requires_confirmation":1,"needs_mobile_confirmation":false,"needs_email_confirmation":true,"email_domain":"gmail.com"}""");
        Assert.True(mail.NeedsEmailConfirmation);
        Assert.Contains("gmail.com", mail.Message);

        var bad = MarketParsers.ParseSellResponse(502, """{"success":false,"message":"You already have a listing for this item pending confirmation."}""");
        Assert.False(bad.Success);
        Assert.Contains("pending confirmation", bad.Message);

        Assert.Contains("Sign in again", MarketParsers.ParseSellResponse(400, "").Message);
    }

    [Fact]
    public void Protection_date_is_read_from_owner_descriptions()
    {
        const string json = """
            {"success":1,"total_inventory_count":1,
             "assets":[{"appid":730,"contextid":"16","assetid":"5","classid":"1","instanceid":"0","amount":"1"}],
             "descriptions":[{"classid":"1","instanceid":"0","name":"Fracture Case","market_hash_name":"Fracture Case","tradable":0,"marketable":0,
               "owner_descriptions":[{"type":"html","value":" "},{"type":"html","value":"⇆ Tradable/Marketable After Mar 16, 2026 (7:00:00) GMT","color":"A75124"}]}]}
            """;
        var item = Assert.Single(InventoryParser.Parse(json).Items);
        Assert.Equal(new DateTimeOffset(2026, 3, 16, 7, 0, 0, TimeSpan.Zero), item.ProtectedUntil);

        const string cache = """
            {"success":1,"assets":[{"contextid":"2","assetid":"6","classid":"2","instanceid":"0"}],
             "descriptions":[{"classid":"2","instanceid":"0","name":"X","marketable":0,"cache_expiration":"2026-03-20T07:00:00Z"}]}
            """;
        Assert.Equal(new DateTimeOffset(2026, 3, 20, 7, 0, 0, TimeSpan.Zero), Assert.Single(InventoryParser.Parse(cache).Items).ProtectedUntil);
    }

    [Fact]
    public void Sell_rules_cover_protected_listed_and_unmarketable()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = ClassifierTests.Skin("1", "AK-47 | Slate", ctx: "16");
        var seen = now.AddDays(-2);
        var p = SellRules.Evaluate(fresh, false, seen, now);
        Assert.Equal(SellState.Protected, p.State);
        Assert.True(p.Estimated);
        Assert.Equal(seen.AddDays(7), p.Until);

        var exact = SellRules.Evaluate(fresh with { ProtectedUntil = now.AddDays(3) }, false, seen, now);
        Assert.False(exact.Estimated);
        Assert.Equal(now.AddDays(3), exact.Until);

        Assert.Equal(SellState.Sellable, SellRules.Evaluate(Sellable("2", "M4A4 | X"), false, null, now).State);
        Assert.Equal(SellState.Listed, SellRules.Evaluate(Sellable("2", "M4A4 | X"), true, null, now).State);
        Assert.Equal(SellState.NotMarketable, SellRules.Evaluate(ClassifierTests.Skin("3", "Name Tag"), false, null, now).State);
    }

    [Fact]
    public void Protection_ending_is_detected_across_asset_id_change()
    {
        var protectedCase = ClassifierTests.Box("10") with { ContextId = "16" };
        var twin = ClassifierTests.Box("11") with { ContextId = "16" };
        var drop = ClassifierTests.Box("12") with { Marketable = true };
        var prev = ClassifierTests.Snap(true, 2, protectedCase, twin);
        var released = ClassifierTests.Box("20") with { Marketable = true };
        var next = ClassifierTests.Snap(true, 3, twin, released, drop);
        var found = SellRules.NewlySellable(prev, next);
        Assert.Equal(["20"], found.Select(i => i.AssetId).ToArray());

        var sameId = ClassifierTests.Skin("30", "AK-47 | Slate");
        var later = SellRules.NewlySellable(ClassifierTests.Snap(true, 1, sameId), ClassifierTests.Snap(true, 1, sameId with { Marketable = true }));
        Assert.Single(later);

        Assert.Empty(SellRules.NewlySellable(ClassifierTests.Snap(false, 1, protectedCase), ClassifierTests.Snap(true, 1, released)));
    }

    [Fact]
    public void Ledger_turns_vanished_listing_into_sale_only_when_item_left()
    {
        var db = new AppDatabase(Path.Combine(_dir, "m.db"));
        var ledger = new MarketLedger(db);
        var sold = Sellable("100", "AK-47 | Slate");
        var kept = Sellable("200", "M4A1-S | Nightmare");
        db.AddDrop(new DropRecord { SteamId = 1, SeenAt = DateTimeOffset.UtcNow.AddDays(-9), Kind = ArrivalKind.WeeklyDrop, Item = sold, PriceAtDrop = 9.5m, Currency = "EUR" });

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        var inv0 = ClassifierTests.Snap(true, 2, sold, kept) is var s0 ? new InventorySnapshot { SteamId = 1, SignedIn = true, TakenAt = t0, Items = s0.Items } : null;
        ledger.ApplyListings(1, [
            new MarketListing { ListingId = "L1", AssetId = "100", Name = sold.Name, MarketHashName = sold.MarketHashName, BuyerPays = 1150, Receive = 1000 },
            new MarketListing { ListingId = "L2", AssetId = "200", Name = kept.Name, MarketHashName = kept.MarketHashName, BuyerPays = 230, Receive = 200, AwaitingConfirmation = true },
        ], "EUR", inv0, t0);
        Assert.Equal(2, db.GetListings(1).Count);
        Assert.Equal(["100", "200"], ledger.ListedAssets(1).Order().ToArray());

        var t1 = t0.AddMinutes(5);
        ledger.ApplyListings(1, [], "EUR", inv0, t1);
        Assert.All(db.GetListings(1), l => Assert.Equal(ListingState.Gone, l.State));

        // A snapshot from before the listings vanished proves nothing.
        Assert.Empty(ledger.Resolve(1, new InventorySnapshot { SteamId = 1, SignedIn = true, TakenAt = t0.AddMinutes(1), Items = [kept] }));
        Assert.Equal(2, db.GetListings(1).Count);

        var sales = ledger.Resolve(1, new InventorySnapshot { SteamId = 1, SignedIn = true, TakenAt = t1.AddMinutes(3), Items = [kept] });
        var sale = Assert.Single(sales);
        Assert.Equal("L1", sale.ListingId);
        Assert.Equal(10m, sale.Receive);
        Assert.Equal(9.5m, sale.ArrivalPrice);
        Assert.Equal(ArrivalKind.WeeklyDrop, sale.ArrivalKind);
        Assert.Empty(db.GetListings(1));
        Assert.Equal("L1", Assert.Single(db.GetSales(1, 10)).ListingId);
    }

    [Fact]
    public void Arrival_is_found_by_fingerprint_after_asset_id_change()
    {
        var db = new AppDatabase(Path.Combine(_dir, "a.db"));
        var original = ClassifierTests.Skin("1", "AK-47 | Slate", ctx: "16");
        db.AddDrop(new DropRecord { SteamId = 1, SeenAt = DateTimeOffset.UtcNow, Kind = ArrivalKind.Purchase, Item = original, PriceAtDrop = 3m, Currency = "EUR" });
        var moved = original with { AssetId = "2", ContextId = "2", Marketable = true };
        Assert.Equal(3m, db.FindArrival(1, moved)!.PriceAtDrop);
        Assert.Null(db.FindArrival(1, ClassifierTests.Skin("3", "AK-47 | Slate")));

        db.TouchFirstSeen(1, ["a", "b"], DateTimeOffset.UtcNow.AddDays(-1));
        db.TouchFirstSeen(1, ["a"], DateTimeOffset.UtcNow);
        var seen = db.GetFirstSeen(1);
        Assert.Equal(2, seen.Count);
        Assert.True(seen["a"] < DateTimeOffset.UtcNow.AddHours(-12));
    }

    [Fact]
    public void Sold_listing_is_not_evidence_of_a_case_opening()
    {
        var box = ClassifierTests.Box("1");
        var skin = ClassifierTests.Skin("2", "AK-47 | Slate");
        var prev = ClassifierTests.Snap(true, 1, box);
        var next = ClassifierTests.Snap(true, 1, skin);
        Assert.Equal(ArrivalKind.CaseOpened, Assert.Single(DropClassifier.Classify(prev, next, true).Added).Kind);
        Assert.Equal(ArrivalKind.WeeklyDrop, Assert.Single(DropClassifier.Classify(prev, next, true, new HashSet<string> { "1" }).Added).Kind);
    }

    [Fact]
    public void Wallet_currency_formats_even_outside_settings_list()
    {
        Assert.Equal("UAH", Currencies.FromSteamCode(18).Code);
        Assert.Equal("1.50₴", Currencies.Format(1.5m, "UAH"));
        Assert.Equal("€1.50", Currencies.Format(1.5m, "EUR"));
        Assert.Equal("#99", Currencies.FromSteamCode(99).Code);
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2", "0.2.0")]
    [InlineData("v1.10.3-beta", "1.10.3")]
    [InlineData("0.1.0+04ef544", "0.1.0")]
    public void Versions_parse(string text, string expected) => Assert.Equal(Version.Parse(expected), AppVersion.Parse(text));

    [Fact]
    public void Update_check_picks_newer_release_with_exe()
    {
        const string json = """
            {"tag_name":"v0.2.0","name":"CS2 Drop Checker 0.2.0","draft":false,"prerelease":false,"html_url":"https://github.com/kaarude/cs2-drop-checker/releases/tag/v0.2.0","body":"Notes",
             "assets":[{"name":"cs2-drop-checker.zip","browser_download_url":"https://github.com/kaarude/cs2-drop-checker/releases/download/v0.2.0/cs2-drop-checker.zip","size":10},
                       {"name":"cs2-drop-checker.exe","browser_download_url":"https://github.com/kaarude/cs2-drop-checker/releases/download/v0.2.0/cs2-drop-checker.exe","size":64000000,"digest":"sha256:ABCDEF"}]}
            """;
        var info = UpdateChecker.ParseLatest(json, new Version(0, 1, 0));
        Assert.NotNull(info);
        Assert.Equal("0.2.0", info!.VersionText);
        Assert.EndsWith("/cs2-drop-checker.exe", info.DownloadUrl);
        Assert.Equal(64000000, info.Size);
        Assert.Equal("abcdef", info.Sha256);
        Assert.Null(UpdateChecker.ParseLatest(json, new Version(0, 2, 0)));
        Assert.Null(UpdateChecker.ParseLatest(json.Replace("\"prerelease\":false", "\"prerelease\":true"), new Version(0, 1, 0)));
        Assert.Null(UpdateChecker.ParseLatest(json.Replace("cs2-drop-checker.exe\"", "other.exe\""), new Version(0, 1, 0)));
    }
}
