using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Detection;
using Cs2DropChecker.Core.Market;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Storage;
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
        _badge = ComputeBadge(item, showProtected: true);
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
    public int Tier { get; }
    public long AssetNumber { get; }
    public decimal Price { get; private set; }

    [ObservableProperty] private string _priceText = "";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _badge;

    /// <summary>Empty when selling is off (Lookup, or not signed in).</summary>
    [ObservableProperty] private string _sellKind = "";
    [ObservableProperty] private string _sellBadge = "";
    [ObservableProperty] private string _sellTip = "";
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _selectMode;

    public PriceQuote? Quote { get; private set; }
    public Sellability? Sell { get; private set; }
    public ListingRow? Listing { get; private set; }
    public bool CanCheck => Sell is { CanSell: true };
    public bool Interactive => Sell is { State: not SellState.NotMarketable };

    public void SetQuote(PriceQuote? q)
    {
        Quote = q;
        Price = q?.Price ?? 0;
        PriceText = q is null ? "—" : Money.Format(q.Price, q.Currency);
        SourceText = q is null ? "no price" : PriceSourceIds.CardName(q.SourceId);
    }

    public void SetSell(Sellability? sell, ListingRow? listing, DateTimeOffset now)
    {
        Sell = sell;
        Listing = listing;
        Badge = ComputeBadge(Item, showProtected: sell is null);
        switch (sell?.State)
        {
            case null:
                SellKind = SellBadge = "";
                SellTip = "Click to compare where to sell";
                break;
            case SellState.Sellable:
                SellKind = "sellable";
                SellBadge = "";
                SellTip = "Click to sell · tick the box to sell several at once";
                break;
            case SellState.Listed:
                var pending = listing?.State == ListingState.Pending;
                SellKind = pending ? "pending" : "listed";
                SellBadge = pending ? "Confirm" : "Listed";
                SellTip = pending ? "Listed, waiting for your confirmation in the Steam Mobile app or email" : "Listed on the Steam Market";
                break;
            case SellState.Protected:
                SellKind = "protected";
                UpdateCountdown(now);
                break;
            default:
                SellKind = "locked";
                SellBadge = "";
                SellTip = "This item can't be sold on the Steam Market";
                break;
        }
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(Interactive));
    }

    public void UpdateCountdown(DateTimeOffset now)
    {
        if (Sell is not { State: SellState.Protected } s) return;
        if (s.Until is { } until)
        {
            SellBadge = (s.Estimated ? "~" : "") + When.Left(until - now);
            SellTip = (s.Estimated ? "Trade protected · sellable in about " : "Trade protected · sellable in ") + When.Left(until - now)
                + " (" + When.Format(until) + ", " + until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + ")";
        }
        else
        {
            SellBadge = "7d";
            SellTip = "Trade protected for up to 7 days after it arrived";
        }
    }

    private static int DropClassifierTier(InventoryItem i) => i.IsStar ? 6 : 5 - InventoryValuation.GroupIndex(i);

    /// <summary>With selling on, the sell badge shows protection instead of "TP".</summary>
    private static string ComputeBadge(InventoryItem item, bool showProtected) =>
        item.IsDoppler ? "Phase?"
        : item.StickerCount > 1 ? $"{item.StickerCount} stickers"
        : item.StickerCount == 1 ? "1 sticker"
        : showProtected && item.ContextId == "16" ? "TP"
        : item.IsStatTrak ? "ST"
        : item.IsSouvenir ? "SV"
        : item.StorageCount is { } n ? $"{n} inside"
        : "";
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

    private static readonly (string Key, string Label)[] SellGroups =
        [("all", "All"), ("sellable", "Sellable"), ("fresh", "Fresh"), ("listed", "Listed")];

    private List<TileViewModel> _tiles = new();
    private List<TileViewModel> _shownTiles = new();
    private readonly HashSet<string> _checked = new(StringComparer.Ordinal);
    private Func<InventoryItem, Sellability>? _sell;
    private Func<string, ListingRow?>? _listing;
    private int _columns = 8;
    private string _selected = "all";
    private string _sellSelected = "all";

    public ItemGridViewModel()
    {
        _sort = SortOptions[0];
        foreach (var (key, label) in SellGroups) SellFilters.Add(new FilterChip(key, label) { IsSelected = key == "all" });
    }

    public ObservableCollection<FilterChip> Filters { get; } = new();
    public ObservableCollection<FilterChip> SellFilters { get; } = new();
    public IReadOnlyList<Option> SortOptions { get; } =
    [
        new("value", "Sort: value"), new("rarity", "Sort: rarity"), new("name", "Sort: name"), new("newest", "Sort: newest"),
    ];

    [ObservableProperty] private IReadOnlyList<TileRow> _rows = [];
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private Option _sort;
    [ObservableProperty] private int _shown;
    [ObservableProperty] private int _total;
    [ObservableProperty] private bool _sellEnabled;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _selectedText = "";
    [ObservableProperty] private int _checkableShown;

    public bool SelectMode => SelectedCount > 0;

    /// <summary>A tile was clicked outside selection mode.</summary>
    public event EventHandler<TileViewModel>? TileOpened;
    public event EventHandler<IReadOnlyList<TileViewModel>>? SellSelectedRequested;

    public IReadOnlyList<TileViewModel> CheckedTiles => _tiles.Where(t => t.IsChecked).ToList();

    public static string GroupKey(InventoryItem i) =>
        i.IsStar ? "star"
        : i.IsContainer ? "case"
        : InventoryValuation.GroupIndex(i) switch { 1 => "covert", 2 => "classified", 3 => "restricted", 4 => "milspec", _ => "other" };

    private static string SellKey(TileViewModel t) => t.Sell?.State switch
    {
        SellState.Sellable => "sellable",
        SellState.Protected => "fresh",
        SellState.Listed => "listed",
        _ => "",
    };

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
        ApplySell(DateTimeOffset.UtcNow);
        Rebuild();
    }

    public void RefreshPrices(Func<string, PriceQuote?> price)
    {
        foreach (var t in _tiles) t.SetQuote(price(t.Item.MarketHashName));
        UpdateSelected();
    }

    /// <summary>Turns selling on (with the rules for each item) or off.</summary>
    public void SetSell(Func<InventoryItem, Sellability>? sell, Func<string, ListingRow?>? listing)
    {
        var wasEnabled = SellEnabled;
        _sell = sell;
        _listing = listing;
        SellEnabled = sell is not null;
        if (!SellEnabled)
        {
            _checked.Clear();
            if (_sellSelected != "all") SelectSellKey("all");
        }
        ApplySell(DateTimeOffset.UtcNow);
        if (wasEnabled != SellEnabled || _sellSelected != "all") Rebuild();
    }

    private void ApplySell(DateTimeOffset now)
    {
        foreach (var t in _tiles)
        {
            t.SetSell(_sell?.Invoke(t.Item), _sell is null ? null : _listing?.Invoke(t.Item.AssetId), now);
            t.IsChecked = t.CanCheck && _checked.Contains(t.Item.AssetId);
        }
        _checked.IntersectWith(_tiles.Where(t => t.IsChecked).Select(t => t.Item.AssetId));
        var counts = _tiles.GroupBy(SellKey).ToDictionary(g => g.Key, g => g.Count());
        foreach (var chip in SellFilters) chip.Count = chip.Key == "all" ? _tiles.Count : counts.GetValueOrDefault(chip.Key);
        UpdateSelected();
    }

    /// <summary>Keeps protection countdowns current.</summary>
    public void TickSell()
    {
        if (!SellEnabled) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var t in _tiles) t.UpdateCountdown(now);
    }

    public void Clear()
    {
        _tiles = new();
        _shownTiles = new();
        _checked.Clear();
        Filters.Clear();
        Rows = [];
        Shown = Total = 0;
        Search = "";
        _selected = "all";
        UpdateSelected();
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
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(SelectMode));

    [RelayCommand]
    private void SelectFilter(FilterChip chip)
    {
        _selected = chip.Key;
        foreach (var f in Filters) f.IsSelected = f == chip;
        Rebuild();
    }

    [RelayCommand]
    private void SelectSellFilter(FilterChip chip)
    {
        SelectSellKey(chip.Key);
        Rebuild();
    }

    private void SelectSellKey(string key)
    {
        _sellSelected = key;
        foreach (var f in SellFilters) f.IsSelected = f.Key == key;
    }

    [RelayCommand]
    private void OpenTile(TileViewModel? tile)
    {
        if (tile is null) return;
        if (SelectMode)
        {
            if (tile.Interactive) ToggleCheck(tile);
            return;
        }
        TileOpened?.Invoke(this, tile);
    }

    [RelayCommand]
    private void ToggleCheck(TileViewModel? tile)
    {
        if (tile is null || !tile.CanCheck) return;
        tile.IsChecked = !tile.IsChecked;
        if (tile.IsChecked) _checked.Add(tile.Item.AssetId);
        else _checked.Remove(tile.Item.AssetId);
        UpdateSelected();
    }

    [RelayCommand]
    private void SelectAllShown()
    {
        foreach (var t in _shownTiles.Where(t => t.CanCheck))
        {
            t.IsChecked = true;
            _checked.Add(t.Item.AssetId);
        }
        UpdateSelected();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var t in _tiles) t.IsChecked = false;
        _checked.Clear();
        UpdateSelected();
    }

    [RelayCommand]
    private void SellSelected()
    {
        var tiles = CheckedTiles;
        if (tiles.Count > 0) SellSelectedRequested?.Invoke(this, tiles);
    }

    private void UpdateSelected()
    {
        var picked = _tiles.Where(t => t.IsChecked).ToList();
        SelectedCount = picked.Count;
        foreach (var t in _tiles) t.SelectMode = picked.Count > 0;
        var worth = picked.Where(t => t.Quote is not null).ToList();
        var currency = worth.FirstOrDefault()?.Quote?.Currency;
        SelectedText = picked.Count == 0 ? ""
            : (picked.Count == 1 ? "1 item selected" : $"{picked.Count} items selected")
              + (worth.Count > 0 ? " · worth about " + Money.Format(worth.Sum(t => t.Price), currency) : "");
        CheckableShown = _shownTiles.Count(t => t.CanCheck && !t.IsChecked);
    }

    private void Rebuild()
    {
        IEnumerable<TileViewModel> q = _tiles;
        if (_selected != "all") q = q.Where(t => GroupKey(t.Item) == _selected);
        if (SellEnabled && _sellSelected != "all") q = q.Where(t => SellKey(t) == _sellSelected);
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
        _shownTiles = list;
        Shown = list.Count;
        Rows = list.Chunk(_columns).Select(c => new TileRow(c)).ToList();
        CheckableShown = _shownTiles.Count(t => t.CanCheck && !t.IsChecked);
    }
}
