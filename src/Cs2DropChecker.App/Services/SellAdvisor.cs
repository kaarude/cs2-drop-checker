using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Selling;

namespace Cs2DropChecker.App.Services;

public static class SellAdvisor
{
    /// <summary>Compares what each marketplace pays out for one item, using the prices already loaded.</summary>
    public static SellAdvice For(PriceService prices, InventoryItem item)
    {
        var cur = prices.Currency;
        var name = item.MarketHashName;
        var steam = prices.Live.TryGetCached(name, cur) ?? prices.Feed.TryGetCached(name, cur);
        var skinport = prices.Skinport.TryGetCached(name, cur);
        return SellCalculator.Compare(steam?.Price, skinport?.Price, cur, prices.Fx.Rate(cur, "EUR"));
    }

    public static string FeeNote =>
        $"Steam adds a {Pct(SellFees.SteamTransactionFee)} Steam fee and a {Pct(SellFees.SteamGameFee)} CS2 fee on top of what you receive. "
        + $"Skinport takes {Pct(SellFees.SkinportFee)} of the sale ({Pct(SellFees.SkinportHighValueFee)} over €{SellFees.SkinportHighValueFromEur:0}). "
        + "Other marketplaces aren't compared because the app has no price feed for them.";

    private static string Pct(decimal fraction) => $"{fraction * 100:0.#}%";
}
