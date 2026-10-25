using System.Globalization;
using System.Text.Json.Serialization;

namespace Cs2DropChecker.Core.Models;

public sealed record InventoryItem
{
    public string AssetId { get; init; } = "";
    public string ContextId { get; init; } = "2";
    public string ClassId { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string MarketHashName { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string TypeTag { get; init; } = "";
    public string TypeName { get; init; } = "";
    public string Weapon { get; init; } = "";
    public string RarityTag { get; init; } = "";
    public string RarityName { get; init; } = "";
    public string RarityColor { get; init; } = "";
    public string QualityTag { get; init; } = "";
    public string Exterior { get; init; } = "";
    public double? Float { get; init; }
    public int? PaintSeed { get; init; }
    public string? CustomName { get; init; }
    public int StickerCount { get; init; }
    public int CharmCount { get; init; }
    public bool Tradable { get; init; }
    public bool Marketable { get; init; }
    public int? StorageCount { get; init; }

    [JsonIgnore] public bool IsStatTrak => QualityTag.Contains("strange", StringComparison.Ordinal);
    [JsonIgnore] public bool IsSouvenir => QualityTag == "tournament";
    [JsonIgnore] public bool IsStar => Name.StartsWith('★');
    [JsonIgnore] public bool IsContainer => TypeTag == "CSGO_Type_WeaponCase";
    [JsonIgnore] public bool IsKey => TypeTag.Contains("KeyTag", StringComparison.Ordinal);
    [JsonIgnore] public bool IsStorageUnit => StorageCount.HasValue || MarketHashName == "Storage Unit";
    [JsonIgnore] public bool HasWear => Float.HasValue || (Exterior.Length > 0 && Exterior != "Not Painted");
    [JsonIgnore] public bool IsDoppler => Name.Contains("Doppler", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hex colour without '#'. Knives and gloves use gold like the in-game reveal.</summary>
    [JsonIgnore] public string RarityHex => IsStar ? "e4ae39" : RarityColor.Length == 6 ? RarityColor : "b0c3d9";

    [JsonIgnore] public string RarityLine => IsStar || IsContainer ? Type : RarityName.Length > 0 ? RarityName : Type;

    [JsonIgnore]
    public string WeaponTitle
    {
        get
        {
            var i = Name.IndexOf(" | ", StringComparison.Ordinal);
            return i > 0 ? Name[..i] : TypeName.Length > 0 ? TypeName : Type;
        }
    }

    [JsonIgnore]
    public string SkinTitle
    {
        get
        {
            var i = Name.IndexOf(" | ", StringComparison.Ordinal);
            return i > 0 ? Name[(i + 3)..] : Name;
        }
    }

    [JsonIgnore]
    public string ExteriorShort => Exterior switch
    {
        "Factory New" => "FN",
        "Minimal Wear" => "MW",
        "Field-Tested" => "FT",
        "Well-Worn" => "WW",
        "Battle-Scarred" => "BS",
        _ => "",
    };

    [JsonIgnore]
    public string WearLine
    {
        get
        {
            var f = Float.HasValue ? Float.Value.ToString("0.0000", CultureInfo.InvariantCulture) : "";
            if (ExteriorShort.Length > 0) return f.Length > 0 ? $"{ExteriorShort} · {f}" : ExteriorShort;
            return f.Length > 0 ? f : IsContainer || !HasWear ? "No wear" : "";
        }
    }

    /// <summary>Things base prices ignore, so the item may be worth more.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ValueFlags
    {
        get
        {
            var flags = new List<string>(3);
            if (IsDoppler) flags.Add("Doppler phase");
            if (StickerCount > 0) flags.Add(StickerCount == 1 ? "1 sticker" : $"{StickerCount} stickers");
            if (CharmCount > 0) flags.Add("charm");
            return flags;
        }
    }

    /// <summary>Identity that survives an asset id change (context move, sticker applied).</summary>
    [JsonIgnore]
    public string Fingerprint => Float.HasValue
        ? $"{MarketHashName}|{Float.Value.ToString("R", CultureInfo.InvariantCulture)}|{PaintSeed}"
        : $"{MarketHashName}|{ClassId}|{InstanceId}";
}

public sealed class InventorySnapshot
{
    public ulong SteamId { get; init; }
    public DateTimeOffset TakenAt { get; init; }
    public bool SignedIn { get; init; }

    /// <summary>Steam's total_inventory_count for context 2. In public mode it also counts items Steam hides.</summary>
    public int TotalCount { get; init; }

    public List<InventoryItem> Items { get; init; } = new();

    [JsonIgnore] public int VisibleMainCount => Items.Count(i => i.ContextId == "2");
    [JsonIgnore] public int HiddenCount => SignedIn ? 0 : Math.Max(0, TotalCount - VisibleMainCount);
}

public enum ArrivalKind
{
    WeeklyDrop,
    NewItem,
    CaseOpened,
    CapsuleOpened,
    SouvenirPackage,
    TradeUp,
    Purchase,
    LatePurchase,
    FromStorage,
    Moved,
}

public static class ArrivalKindText
{
    public static string Label(ArrivalKind kind) => kind switch
    {
        ArrivalKind.WeeklyDrop => "Weekly drop",
        ArrivalKind.NewItem => "New item",
        ArrivalKind.CaseOpened => "Case opened",
        ArrivalKind.CapsuleOpened => "Capsule opened",
        ArrivalKind.SouvenirPackage => "Souvenir package",
        ArrivalKind.TradeUp => "Trade-up",
        ArrivalKind.Purchase => "Bought / traded",
        ArrivalKind.LatePurchase => "Bought / traded",
        ArrivalKind.FromStorage => "From storage",
        _ => "Moved",
    };

    public static string CardTag(ArrivalKind kind) => kind switch
    {
        ArrivalKind.SouvenirPackage => "Souvenir pkg",
        ArrivalKind.CapsuleOpened => "Capsule",
        _ => Label(kind),
    };

    public static bool ShowsCard(ArrivalKind kind) => kind is not (ArrivalKind.FromStorage or ArrivalKind.Moved);

    public static bool IsDropLike(ArrivalKind kind) => kind is ArrivalKind.WeeklyDrop or ArrivalKind.NewItem;

    public static bool IsOpening(ArrivalKind kind) => kind is ArrivalKind.CaseOpened or ArrivalKind.CapsuleOpened or ArrivalKind.SouvenirPackage;

    public static bool IsPurchase(ArrivalKind kind) => kind is ArrivalKind.Purchase or ArrivalKind.LatePurchase;
}

public sealed record DropRecord
{
    public long Id { get; init; }
    public ulong SteamId { get; init; }
    public DateTimeOffset SeenAt { get; init; }
    public ArrivalKind Kind { get; init; }
    public bool SeenLate { get; init; }
    public InventoryItem Item { get; init; } = new();
    public decimal? PriceAtDrop { get; init; }
    public string? Currency { get; init; }
    public string? PriceSource { get; init; }
}

public sealed record SteamProfile(ulong SteamId, string Name, string AvatarUrl, string Privacy);
