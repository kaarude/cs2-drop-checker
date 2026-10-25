using Cs2DropChecker.Core.Detection;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Valuation;

namespace Cs2DropChecker.Core.Tests;

public class ClassifierTests
{
    private static int _seed = 1;

    internal static InventoryItem Skin(string asset, string name, string rarity = "Rarity_Rare_Weapon", string ctx = "2", double? wear = null, bool tradable = true) => new()
    {
        AssetId = asset,
        ContextId = ctx,
        ClassId = "c" + asset,
        InstanceId = "0",
        Name = name,
        MarketHashName = name + " (Field-Tested)",
        RarityTag = rarity,
        Exterior = "Field-Tested",
        Float = wear ?? 0.1 + (_seed++ % 500) / 1000.0,
        PaintSeed = _seed,
        TypeTag = name.StartsWith('★') ? "CSGO_Type_Knife" : "CSGO_Type_Rifle",
        Tradable = tradable,
    };

    internal static InventoryItem Box(string asset, string name = "Kilowatt Case") => new()
    {
        AssetId = asset,
        ClassId = "box-" + name,
        InstanceId = "0",
        Name = name,
        MarketHashName = name,
        TypeTag = "CSGO_Type_WeaponCase",
        RarityTag = "Rarity_Common",
        Type = "Base Grade Container",
    };

    internal static InventoryItem Unit(string asset, int count) => new()
    {
        AssetId = asset,
        ClassId = "unit",
        InstanceId = "i" + count,
        Name = "Storage Unit",
        MarketHashName = "Storage Unit",
        TypeTag = "CSGO_Type_Tool",
        StorageCount = count,
    };

    internal static InventorySnapshot Snap(bool signedIn, int total, params InventoryItem[] items) => new()
    {
        SteamId = 1,
        TakenAt = DateTimeOffset.UtcNow,
        SignedIn = signedIn,
        TotalCount = total,
        Items = items.ToList(),
    };

    [Fact]
    public void New_item_while_playing_is_a_weekly_drop()
    {
        var a = Skin("1", "AK-47 | Slate");
        var prev = Snap(false, 1, a);
        var next = Snap(false, 2, a, Skin("2", "P250 | Sand Dune", "Rarity_Common_Weapon"));
        var d = DropClassifier.Classify(prev, next, cs2Active: true);
        var only = Assert.Single(d.Added);
        Assert.Equal(ArrivalKind.WeeklyDrop, only.Kind);
        Assert.Equal(ArrivalKind.NewItem, Assert.Single(DropClassifier.Classify(prev, next, false).Added).Kind);
    }

    [Fact]
    public void Consumed_case_means_case_opened()
    {
        var box = Box("10");
        var prev = Snap(false, 2, box, Skin("1", "AK-47 | Slate"));
        var next = Snap(false, 2, prev.Items[1], Skin("11", "StatTrak™ M4A4 | Temukau", "Rarity_Ancient_Weapon"));
        var only = Assert.Single(DropClassifier.Classify(prev, next, true).Added);
        Assert.Equal(ArrivalKind.CaseOpened, only.Kind);
        Assert.Equal("11", only.Item.AssetId);
    }

    [Fact]
    public void Souvenir_package_and_capsule_are_recognised()
    {
        var pkg = Box("20", "Austin 2025 Inferno Souvenir Package");
        var cap = Box("21", "Austin 2025 Legends Sticker Capsule");
        var souvenir = Skin("30", "Souvenir P250 | Sand Dune", "Rarity_Common_Weapon") with { QualityTag = "tournament" };
        var sticker = new InventoryItem { AssetId = "31", ClassId = "st", Name = "Sticker | Vitality | Austin 2025", MarketHashName = "Sticker | Vitality | Austin 2025", TypeTag = "CSGO_Tool_Sticker" };
        var prev = Snap(false, 2, pkg, cap);
        var next = Snap(false, 2, souvenir, sticker);
        var d = DropClassifier.Classify(prev, next, true).Added.ToDictionary(x => x.Item.AssetId, x => x.Kind);
        Assert.Equal(ArrivalKind.SouvenirPackage, d["30"]);
        Assert.Equal(ArrivalKind.CapsuleOpened, d["31"]);
    }

    [Fact]
    public void Ten_consumed_skins_and_a_higher_tier_result_is_a_trade_up()
    {
        var inputs = Enumerable.Range(0, 10).Select(i => Skin("in" + i, "MP9 | Slide", "Rarity_Mythical_Weapon")).ToArray();
        var keep = Skin("k", "AWP | Safari Mesh", "Rarity_Common_Weapon");
        var prev = Snap(false, 11, [.. inputs, keep]);
        var next = Snap(false, 2, keep, Skin("out", "M4A1-S | Decimator", "Rarity_Legendary_Weapon"));
        var only = Assert.Single(DropClassifier.Classify(prev, next, true).Added);
        Assert.Equal(ArrivalKind.TradeUp, only.Kind);
    }

    [Fact]
    public void Five_coverts_into_a_knife_is_a_trade_up()
    {
        var inputs = Enumerable.Range(0, 5).Select(i => Skin("c" + i, "AWP | Asiimov", "Rarity_Ancient_Weapon")).ToArray();
        var prev = Snap(false, 5, inputs);
        var next = Snap(false, 1, Skin("knife", "★ Karambit | Doppler", "Rarity_Ancient_Weapon"));
        Assert.Equal(ArrivalKind.TradeUp, Assert.Single(DropClassifier.Classify(prev, next, true).Added).Kind);
    }

    [Fact]
    public void Same_float_with_new_asset_id_is_a_move_not_a_drop()
    {
        var a = Skin("1", "AK-47 | Slate", wear: 0.0934);
        var moved = a with { AssetId = "99", ContextId = "2" };
        var d = DropClassifier.Classify(Snap(true, 1, a with { ContextId = "16" }), Snap(true, 1, moved), true);
        Assert.Equal(ArrivalKind.Moved, Assert.Single(d.Added).Kind);
    }

    [Fact]
    public void Items_taken_out_of_storage_are_not_drops()
    {
        var prev = Snap(false, 1, Unit("u", 10));
        var next = Snap(false, 3, Unit("u", 8), Skin("a", "AK-47 | Slate"), Skin("b", "Glock-18 | Clear Polymer"));
        Assert.All(DropClassifier.Classify(prev, next, true).Added, x => Assert.Equal(ArrivalKind.FromStorage, x.Kind));
    }

    [Fact]
    public void Item_that_steam_stops_hiding_is_a_late_purchase()
    {
        var a = Skin("1", "AK-47 | Slate");
        var prev = Snap(false, 2, a);
        var next = Snap(false, 2, a, Skin("2", "★ Sport Gloves | Vice", "Rarity_Ancient"));
        Assert.Equal(1, prev.HiddenCount);
        var only = Assert.Single(DropClassifier.Classify(prev, next, true).Added);
        Assert.Equal(ArrivalKind.LatePurchase, only.Kind);
        Assert.True(only.SeenLate);
    }

    [Fact]
    public void Newly_hidden_items_are_counted()
    {
        var a = Skin("1", "AK-47 | Slate");
        var d = DropClassifier.Classify(Snap(false, 1, a), Snap(false, 3, a), true);
        Assert.Empty(d.Added);
        Assert.Equal(2, d.NewlyHidden);
    }

    [Fact]
    public void Signed_in_protected_context_item_is_a_purchase()
    {
        var a = Skin("1", "AK-47 | Slate");
        var d = DropClassifier.Classify(Snap(true, 1, a), Snap(true, 1, a, Skin("2", "AWP | Asiimov", "Rarity_Ancient_Weapon", ctx: "16")), true);
        Assert.Equal(ArrivalKind.Purchase, Assert.Single(d.Added).Kind);
    }

    [Fact]
    public void Switching_to_signed_in_does_not_report_protected_items()
    {
        var a = Skin("1", "AK-47 | Slate");
        var prev = Snap(false, 3, a);
        var next = Snap(true, 1, a, Skin("2", "AWP | Asiimov", ctx: "16"), Skin("3", "M4A1-S | Decimator", ctx: "16"));
        Assert.Empty(DropClassifier.Classify(prev, next, true).Added);
    }

    [Fact]
    public void Removed_items_are_reported()
    {
        var a = Skin("1", "AK-47 | Slate");
        var b = Skin("2", "AWP | Asiimov");
        var d = DropClassifier.Classify(Snap(false, 2, a, b), Snap(false, 1, a), true);
        Assert.Empty(d.Added);
        Assert.Equal("2", Assert.Single(d.Removed).AssetId);
    }

    [Fact]
    public void Valuation_groups_and_totals()
    {
        var items = new[]
        {
            Skin("1", "★ Karambit | Doppler", "Rarity_Ancient_Weapon"),
            Skin("2", "AWP | Asiimov", "Rarity_Ancient_Weapon"),
            Skin("3", "M4A1-S | Decimator", "Rarity_Legendary_Weapon"),
            Skin("4", "AK-47 | Slate", "Rarity_Mythical_Weapon"),
            Skin("5", "Glock-18 | Clear Polymer", "Rarity_Rare_Weapon"),
            Box("6"),
        };
        var prices = new Dictionary<string, decimal> { ["1"] = 1000m, ["2"] = 100m, ["3"] = 10m, ["4"] = 5m, ["5"] = 1m };
        var v = InventoryValuation.Compute(items, i => prices.TryGetValue(i.AssetId, out var p) ? p : null);
        Assert.Equal(1116m, v.Total);
        Assert.Equal(5, v.PricedCount);
        Assert.Equal(6, v.ItemCount);
        Assert.Equal("1", v.MostValuable!.AssetId);
        Assert.Equal([1000m, 100m, 10m, 5m, 1m, 0m], v.Groups.Select(g => g.Value).ToArray());
        Assert.Equal(1, v.Groups[5].Count);
    }
}
