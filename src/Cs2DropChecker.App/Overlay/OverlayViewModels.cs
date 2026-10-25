using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Cs2DropChecker.App.Controls;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.App.ViewModels;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Valuation;

namespace Cs2DropChecker.App.Overlay;

/// <summary>One in-game card: what arrived, how, and what it is worth.</summary>
public sealed partial class CardViewModel : ObservableObject
{
    private static readonly Brush GoldBorder = Frozen(new LinearGradientBrush(
        Color.FromRgb(0xf5, 0xcf, 0x6a), Color.FromRgb(0xe4, 0xae, 0x39), 90));

    public CardViewModel(DropRecord d, bool test = false, bool sellable = false)
    {
        Drop = d;
        var i = d.Item;
        DropId = d.Id;
        IsTest = test;
        Sellable = sellable;
        Gold = i.IsStar;
        RarityHex = i.RarityHex;
        Tag = (Gold ? "★ " : "") + (test ? "Test card" : sellable ? "Now sellable" : ArrivalKindText.CardTag(d.Kind));
        IconUrl = i.IconUrl;
        RarityLine = i.RarityLine;
        Weapon = i.WeaponTitle;
        Skin = i.SkinTitle;
        WearChip = i.HasWear || i.Float.HasValue ? ItemText.WearChip(i) : i.IsContainer || !i.HasWear ? "No wear" : "";
        StatTrak = i.IsStatTrak;
        Souvenir = i.IsSouvenir;
        if (sellable)
        {
            Warn = "Trade protection over · ready to list";
            WarnGood = true;
        }
        else if (ItemText.ValueWarning(i) is { } warn)
        {
            Warn = warn;
        }
        else if (d.SeenLate)
        {
            Warn = "Seen 7 days after purchase";
            WarnLate = true;
        }
        else if (i.ContextId == "16")
        {
            Warn = "Trade protected for 7 days";
            WarnLate = true;
        }
        var c = BrushCache.ColorFromHex(RarityHex);
        RarityBrush = BrushCache.FromHex(RarityHex);
        BorderBrush = Gold ? GoldBorder : Frozen(new LinearGradientBrush(c, Color.FromArgb(0x90, c.R, c.G, c.B), 90));
        TintBrush = BrushCache.Tint(RarityHex, Gold ? (byte)0x80 : (byte)0x6a);
        TagBrush = BrushCache.FromHex(sellable ? "8fd19a" : Gold ? "e4ae39" : "de9b35");
        if (d.PriceAtDrop is { } p)
        {
            Price = Money.Format(p, d.Currency);
            Source = PriceSourceIds.CardName(d.PriceSource);
            Pending = false;
        }
        UpdateAge();
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    public DropRecord Drop { get; }
    public long DropId { get; }
    public bool IsTest { get; }
    public bool Gold { get; }
    public string RarityHex { get; }
    public string Tag { get; }
    public string IconUrl { get; }
    public string RarityLine { get; }
    public string Weapon { get; }
    public string Skin { get; }
    public string SkinUpper => Skin.ToUpperInvariant();
    /// <summary>Names that do not fit one line on the card (cases, capsules, long sticker names) wrap instead.</summary>
    public bool LongSkin => Skin.Length > 17;
    public string WearChip { get; }
    public bool StatTrak { get; }
    public bool Souvenir { get; }
    public string? Warn { get; }
    public string WarnUpper => Warn?.ToUpperInvariant() ?? "";
    public bool WarnLate { get; }
    public bool WarnGood { get; }
    /// <summary>A reminder that an item left trade protection, not an arrival.</summary>
    public bool Sellable { get; }
    public Brush RarityBrush { get; }
    public Brush BorderBrush { get; }
    public Brush TintBrush { get; }
    public Brush TagBrush { get; }
    public decimal SortValue { get; private set; }

    [ObservableProperty] private string _price = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private bool _pending = true;
    [ObservableProperty] private string _age = "just now";
    [ObservableProperty] private double _remaining = 1;

    public void SetQuote(PriceQuote? q)
    {
        Pending = false;
        if (q is null)
        {
            Price = Sellable ? "—" : "No price";
            Source = Sellable ? "" : "Not on the market";
            return;
        }
        SortValue = q.Price;
        Price = Money.Format(q.Price, q.Currency);
        Source = PriceSourceIds.CardName(q.SourceId);
    }

    public void UpdateAge()
    {
        var span = DateTimeOffset.UtcNow - Drop.SeenAt;
        Age = IsTest ? "preview" : span < TimeSpan.FromSeconds(15) ? "just now" : When.Ago(Drop.SeenAt).Replace(" ago", "", StringComparison.Ordinal);
    }
}

public sealed record LegendRow(string Name, string ColorHex, string Value);

public sealed partial class ValuePanelViewModel : ObservableObject
{
    [ObservableProperty] private string _total = "—";
    [ObservableProperty] private string _priced = "";
    [ObservableProperty] private string _delta = "";
    [ObservableProperty] private bool _deltaDown;
    [ObservableProperty] private bool _hasDelta;
    [ObservableProperty] private IReadOnlyList<ValueGroup> _groups = InventoryValuation.EmptyGroups();
    [ObservableProperty] private IReadOnlyList<LegendRow> _legend = [];
    [ObservableProperty] private string _footer = "";

    public void Fill(AppServices s)
    {
        var w = s.Watcher;
        var v = w.Value;
        var cur = s.Settings.Currency;
        Total = w.Current is null ? "—" : Money.Format(v.Total, cur);
        Priced = $"{v.PricedCount}/{v.ItemCount} priced";
        var (delta, newToday) = w.Today();
        var items = newToday == 1 ? "1 new item today" : $"{newToday} new items today";
        if (delta is { } d && d != 0)
        {
            DeltaDown = d < 0;
            HasDelta = true;
            Delta = Money.Format(Math.Abs(d), cur) + " · " + items;
        }
        else
        {
            DeltaDown = false;
            HasDelta = false;
            Delta = items;
        }
        Groups = v.Groups;
        Legend = v.Groups.Select(g => new LegendRow(g.Name, g.ColorHex, Money.Format(g.Value, cur))).ToList();
        Footer = $"Src · {PriceSourceIds.CardName(w.ValueSourceId)} · {s.Settings.Hotkeys.ShowTotalValue}";
    }
}

public sealed partial class OverlayViewModel : ObservableObject
{
    [ObservableProperty] private CardViewModel? _card;
    [ObservableProperty] private int _queued;
    [ObservableProperty] private bool _showValue;
    [ObservableProperty] private string _toast = "";
    [ObservableProperty] private string _toastKey = "";
    [ObservableProperty] private bool _toastOn;
    [ObservableProperty] private double _scale = 0.8;

    public ValuePanelViewModel Value { get; } = new();

    public string QueueText => $"+{Queued} queued";
    public bool ShowQueue => Card is not null && Queued > 0;

    partial void OnQueuedChanged(int value)
    {
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(ShowQueue));
    }

    partial void OnCardChanged(CardViewModel? value) => OnPropertyChanged(nameof(ShowQueue));
}
