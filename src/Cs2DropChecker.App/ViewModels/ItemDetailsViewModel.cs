using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Selling;

namespace Cs2DropChecker.App.ViewModels;

public sealed record SellRow(string Name, string ListPrice, string Fee, string Net, string Payout, bool Best);

/// <summary>Detail panel for one item: price, where it pays most to sell, and shortcuts to share or set an alert.</summary>
public sealed class ItemDetailsViewModel
{
    public ItemDetailsViewModel(InventoryItem item, PriceQuote? now, SellAdvice advice, DropRecord? drop = null, bool own = true)
    {
        Item = item;
        CanShare = own;
        Drop = drop;
        IconUrl = item.IconUrl;
        Name = item.Name;
        Wear = ItemText.WearLong(item);
        RarityHex = item.RarityHex;
        RarityText = ItemText.RarityShort(item);
        PriceText = Money.Format(now);
        SourceText = now is null ? "no price" : PriceSourceIds.TableName(now.SourceId);
        if (drop is not null)
        {
            How = $"{ArrivalKindText.Label(drop.Kind)} · {When.Format(drop.SeenAt)}";
            AtDrop = drop.PriceAtDrop is { } p ? Money.Format(p, drop.Currency) : "—";
        }
        var cur = advice.Currency;
        Rows = advice.Options
            .Select(o => new SellRow(o.Name, Money.Format(o.ListPrice, cur), "-" + Money.Format(o.Fee, cur), Money.Format(o.Net, cur), o.Payout, o == advice.Best && advice.Options.Count > 1))
            .ToList();
        Headline = advice.Headline;
        HasBest = advice.Best is not null && advice.Options.Count > 1 && advice.Margin is > 0;
        Note = item.ContextId == "16" ? "Trade protected: it can't be sold until the hold ends."
            : !item.Marketable && !item.Tradable ? "Steam marks this item as not marketable."
            : ItemText.ValueWarning(item) is { } w ? w + " · the base price ignores this."
            : "";
    }

    public InventoryItem Item { get; }
    public DropRecord? Drop { get; }
    public bool IsDrop => Drop is not null;
    public bool CanShare { get; }
    public string IconUrl { get; }
    public string Name { get; }
    public string Wear { get; }
    public string RarityHex { get; }
    public string RarityText { get; }
    public string PriceText { get; }
    public string SourceText { get; }
    public string How { get; } = "";
    public string AtDrop { get; } = "";
    public IReadOnlyList<SellRow> Rows { get; }
    public string Headline { get; }
    public bool HasBest { get; }
    public string Note { get; }
    public string FeeNote => SellAdvisor.FeeNote;

    /// <summary>Set <paramref name="own"/> to false for someone else's item (Lookup): no share card for it.</summary>
    public static ItemDetailsViewModel For(AppServices s, InventoryItem item, DropRecord? drop = null, bool own = true) =>
        new(item, s.Prices.GetCached(item.MarketHashName), SellAdvisor.For(s.Prices, item), drop, own);
}
