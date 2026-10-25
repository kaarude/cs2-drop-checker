using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Detection;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Valuation;

namespace Cs2DropChecker.App.ViewModels;

public sealed record Flag(string Text, string Kind);

public sealed record Option(object Value, string Label)
{
    public override string ToString() => Label;
}

public static class ItemText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<Flag> Flags(InventoryItem item, bool seenLate)
    {
        var f = new List<Flag>(4);
        if (item.IsStatTrak) f.Add(new Flag("StatTrak™", "st"));
        if (item.IsSouvenir) f.Add(new Flag("Souvenir", "sv"));
        if (item.IsDoppler) f.Add(new Flag("Phase not priced", "warn"));
        if (item.StickerCount > 0) f.Add(new Flag(item.StickerCount == 1 ? "1 sticker" : $"{item.StickerCount} stickers", "warn"));
        if (item.CharmCount > 0) f.Add(new Flag("Charm", "warn"));
        if (seenLate) f.Add(new Flag("Seen 7 d late", "late"));
        if (item.ContextId == "16") f.Add(new Flag("Trade protected", "late"));
        return f;
    }

    public static string WearLong(InventoryItem i)
    {
        if (i.Exterior.Length > 0 && i.Exterior != "Not Painted")
            return i.Float is { } f ? $"{i.Exterior} · {f.ToString("0.0000", Inv)}" : i.Exterior;
        if (i.Float is { } g) return g.ToString("0.0000", Inv);
        return i.TypeName.Length > 0 ? i.TypeName : i.Type;
    }

    public static string WearChip(InventoryItem i)
    {
        var f = i.Float.HasValue ? i.Float.Value.ToString("0.0000", Inv) : "";
        if (i.ExteriorShort.Length > 0) return f.Length > 0 ? $"{i.ExteriorShort} {f}" : i.ExteriorShort;
        return f.Length > 0 ? f : "No wear";
    }

    public static string RarityShort(InventoryItem i)
    {
        if (i.IsStar) return i.Type.Replace("StatTrak™ ", "", StringComparison.Ordinal);
        var r = i.RarityName.Length > 0 ? i.RarityName : i.Type;
        return r.Replace(" Grade", "", StringComparison.Ordinal);
    }

    public static string? ValueWarning(InventoryItem i)
    {
        var flags = i.ValueFlags;
        return flags.Count == 0 ? null : "May be worth more · " + string.Join(", ", flags);
    }

    public static string Initials(string name)
    {
        var letters = name.Where(char.IsLetterOrDigit).Take(2).ToArray();
        return letters.Length == 0 ? "?" : new string(letters).ToUpperInvariant();
    }
}

public sealed partial class DropRowViewModel : ObservableObject
{
    public DropRowViewModel(DropRecord d, PriceQuote? now)
    {
        Record = d;
        var i = d.Item;
        When = Services.When.Format(d.SeenAt);
        IconUrl = i.IconUrl;
        Name = i.Name;
        Wear = ItemText.WearLong(i);
        How = ArrivalKindText.Label(d.Kind);
        RarityHex = i.RarityHex;
        Flags = ItemText.Flags(i, d.SeenLate);
        AtDrop = d.PriceAtDrop is { } p ? Money.Format(p, d.Currency) : "—";
        SetNow(now);
    }

    public DropRecord Record { get; private set; }
    public long Id => Record.Id;
    public string When { get; }
    public string IconUrl { get; }
    public string Name { get; }
    public string Wear { get; }
    public string How { get; }
    public string RarityHex { get; }
    public IReadOnlyList<Flag> Flags { get; }

    [ObservableProperty] private string _atDrop = "—";
    [ObservableProperty] private string _now = "—";
    [ObservableProperty] private string _trend = "";
    [ObservableProperty] private string _source = "—";

    public void SetNow(PriceQuote? q)
    {
        Now = q is null ? "—" : Money.Format(q.Price, q.Currency);
        var at = Record.PriceAtDrop;
        Trend = q is not null && at is { } a && Record.Currency == q.Currency
            ? q.Price > a ? "up" : q.Price < a ? "down" : ""
            : "";
        Source = PriceSourceIds.TableName(Record.PriceSource ?? q?.SourceId);
    }

    public void SetDropPrice(PriceQuote? q)
    {
        if (q is null)
        {
            AtDrop = "no price";
            return;
        }
        Record = Record with { PriceAtDrop = q.Price, Currency = q.Currency, PriceSource = q.SourceId };
        AtDrop = Money.Format(q.Price, q.Currency);
        Source = PriceSourceIds.TableName(q.SourceId);
    }
}

public sealed partial class TileViewModel : ObservableObject
{
    public TileViewModel(InventoryItem item, PriceQuote? q)
    {
        Item = item;
        IconUrl = item.IconUrl;
        RarityText = ItemText.RarityShort(item);
        Weapon = item.WeaponTitle;
        Skin = item.SkinTitle;
        Wear = item.WearLine;
        RarityHex = item.RarityHex;
        Gold = item.IsStar;
        Tier = DropClassifierTier(item);
        Badge = item.IsDoppler ? "Phase?"
            : item.StickerCount > 1 ? $"{item.StickerCount} stickers"
            : item.StickerCount == 1 ? "1 sticker"
            : item.ContextId == "16" ? "TP"
            : item.IsStatTrak ? "ST"
            : item.IsSouvenir ? "SV"
            : item.StorageCount is { } n ? $"{n} inside"
            : "";
        AssetNumber = long.TryParse(item.AssetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : 0;
        SetQuote(q);
    }

    public InventoryItem Item { get; }
    public string IconUrl { get; }
    public string RarityText { get; }
    public string Weapon { get; }
    public string Skin { get; }
    public string Wear { get; }
    public string RarityHex { get; }
    public bool Gold { get; }
    public string Badge { get; }
    public int Tier { get; }
    public long AssetNumber { get; }
    public decimal Price { get; private set; }

    [ObservableProperty] private string _priceText = "";
    [ObservableProperty] private string _sourceText = "";

    public void SetQuote(PriceQuote? q)
    {
        Price = q?.Price ?? 0;
        PriceText = q is null ? "—" : Money.Format(q.Price, q.Currency);
        SourceText = q is null ? "no price" : PriceSourceIds.CardName(q.SourceId);
    }

    private static int DropClassifierTier(InventoryItem i) => i.IsStar ? 6 : 5 - InventoryValuation.GroupIndex(i);
}

public sealed record TileRow(IReadOnlyList<TileViewModel> Tiles);

public sealed partial class FilterChip(string key, string label) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;
    public string Display => $"{Label} {Count}";
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Display));
}

/// <summary>Filterable, sortable tile grid. Rows of N tiles keep the list virtualized.</summary>
public sealed partial class ItemGridViewModel : ObservableObject
{
    private static readonly (string Key, string Label)[] Groups =
    [
        ("all", "All"), ("star", "Knives & gloves"), ("covert", "Covert"), ("classified", "Classified"),
        ("restricted", "Restricted"), ("milspec", "Mil-Spec"), ("case", "Cases"), ("other", "Other"),
    ];

    private List<TileViewModel> _tiles = new();
    private int _columns = 8;
    private string _selected = "all";

    public ItemGridViewModel()
    {
        _sort = SortOptions[0];
    }

    public ObservableCollection<FilterChip> Filters { get; } = new();
    public IReadOnlyList<Option> SortOptions { get; } =
    [
        new("value", "Sort: value"), new("rarity", "Sort: rarity"), new("name", "Sort: name"), new("newest", "Sort: newest"),
    ];

    [ObservableProperty] private IReadOnlyList<TileRow> _rows = [];
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private Option _sort;
    [ObservableProperty] private int _shown;
    [ObservableProperty] private int _total;

    public static string GroupKey(InventoryItem i) =>
        i.IsStar ? "star"
        : i.IsContainer ? "case"
        : InventoryValuation.GroupIndex(i) switch { 1 => "covert", 2 => "classified", 3 => "restricted", 4 => "milspec", _ => "other" };

    public void SetItems(IEnumerable<InventoryItem> items, Func<string, PriceQuote?> price)
    {
        _tiles = items.Select(i => new TileViewModel(i, price(i.MarketHashName))).ToList();
        Total = _tiles.Count;
        var counts = _tiles.GroupBy(t => GroupKey(t.Item)).ToDictionary(g => g.Key, g => g.Count());
        Filters.Clear();
        foreach (var (key, label) in Groups)
        {
            var n = key == "all" ? _tiles.Count : counts.GetValueOrDefault(key);
            if (key != "all" && n == 0) continue;
            Filters.Add(new FilterChip(key, label) { Count = n });
        }
        if (Filters.All(f => f.Key != _selected)) _selected = "all";
        foreach (var f in Filters) f.IsSelected = f.Key == _selected;
        Rebuild();
    }

    public void RefreshPrices(Func<string, PriceQuote?> price)
    {
        foreach (var t in _tiles) t.SetQuote(price(t.Item.MarketHashName));
    }

    public void Clear()
    {
        _tiles = new();
        Filters.Clear();
        Rows = [];
        Shown = Total = 0;
        Search = "";
        _selected = "all";
    }

    public void SetColumns(int columns)
    {
        columns = Math.Clamp(columns, 1, 20);
        if (columns == _columns) return;
        _columns = columns;
        Rebuild();
    }

    partial void OnSearchChanged(string value) => Rebuild();
    partial void OnSortChanged(Option value) => Rebuild();

    [RelayCommand]
    private void SelectFilter(FilterChip chip)
    {
        _selected = chip.Key;
        foreach (var f in Filters) f.IsSelected = f == chip;
        Rebuild();
    }

    private void Rebuild()
    {
        IEnumerable<TileViewModel> q = _tiles;
        if (_selected != "all") q = q.Where(t => GroupKey(t.Item) == _selected);
        var s = Search.Trim();
        if (s.Length > 0) q = q.Where(t => t.Item.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || t.Item.Type.Contains(s, StringComparison.OrdinalIgnoreCase));
        q = (Sort?.Value as string) switch
        {
            "rarity" => q.OrderByDescending(t => t.Tier).ThenByDescending(t => t.Price),
            "name" => q.OrderBy(t => t.Item.Name, StringComparer.OrdinalIgnoreCase),
            "newest" => q.OrderByDescending(t => t.AssetNumber),
            _ => q.OrderByDescending(t => t.Price).ThenByDescending(t => t.Tier),
        };
        var list = q.ToList();
        Shown = list.Count;
        Rows = list.Chunk(_columns).Select(c => new TileRow(c)).ToList();
    }
}
