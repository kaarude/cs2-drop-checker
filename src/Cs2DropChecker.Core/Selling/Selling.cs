using Cs2DropChecker.Core.Pricing;

namespace Cs2DropChecker.Core.Selling;

/// <summary>
/// Seller fees per marketplace, in one place. Checked against each site's own documentation on 2026-10-25:
/// Steam Support "Community Market FAQ" (help.steampowered.com/en/faqs/view/61F0-72B7-9A18-C70B) and
/// Skinport "What is the sales fee?" (skinport.com/faq/sales-fee) plus its "Lower Fees for Everybody" post.
/// </summary>
public static class SellFees
{
    /// <summary>Steam Transaction Fee, minimum one cent.</summary>
    public const decimal SteamTransactionFee = 0.05m;

    /// <summary>Counter-Strike 2 game fee collected by Valve as publisher.</summary>
    public const decimal SteamGameFee = 0.10m;

    public const decimal SteamMinimumFee = 0.01m;

    public const decimal SkinportFee = 0.08m;

    public const decimal SkinportHighValueFee = 0.06m;

    /// <summary>Skinport rounds its fee up to the cent, so every sale costs at least one cent.</summary>
    public const decimal SkinportMinimumFee = 0.01m;

    /// <summary>Skinport charges the lower fee on items over €1000.</summary>
    public const decimal SkinportHighValueFromEur = 1000m;
}

public enum SellVenue { SteamMarket, Skinport }

public sealed record SellOption(SellVenue Venue, string Name, decimal ListPrice, decimal Fee, decimal Net, string Payout);

public sealed record SellAdvice(IReadOnlyList<SellOption> Options, SellOption? Best, decimal? Margin, string Currency)
{
    public static SellAdvice None(string currency) => new([], null, null, currency);

    /// <summary>"Sell on Skinport, +£3.20 vs Steam".</summary>
    public string Headline
    {
        get
        {
            if (Best is null) return "No price to compare";
            if (Options.Count == 1) return $"Only {Best.Name} has a price";
            var other = Options.First(o => o != Best);
            if (Margin is not { } m || m == 0) return $"{Best.Name} and {other.Name} pay the same";
            return $"Sell on {Best.Name}, +{Currencies.Format(m, Currency)} vs {ShortName(other.Venue)}";
        }
    }

    private static string ShortName(SellVenue v) => v == SellVenue.SteamMarket ? "Steam" : "Skinport";
}

public static class SellCalculator
{
    /// <summary>
    /// What the seller receives when a buyer pays <paramref name="buyerPrice"/> on the Steam Market. Steam adds both fees on
    /// top of the seller's amount and rounds each one down to the cent with a one cent minimum, like the market's sell dialog.
    /// </summary>
    public static decimal SteamNet(decimal buyerPrice)
    {
        var paid = (long)Math.Floor(buyerPrice * 100m);
        if (paid < 3) return 0;
        var receive = (long)Math.Floor(paid / (1m + SellFees.SteamTransactionFee + SellFees.SteamGameFee));
        while (receive > 1 && BuyerPays(receive) > paid) receive--;
        while (BuyerPays(receive + 1) <= paid) receive++;
        return receive / 100m;
    }

    private static long BuyerPays(long receiveCents)
    {
        var min = (long)(SellFees.SteamMinimumFee * 100m);
        var steam = Math.Max(min, (long)Math.Floor(receiveCents * SellFees.SteamTransactionFee));
        var game = Math.Max(min, (long)Math.Floor(receiveCents * SellFees.SteamGameFee));
        return receiveCents + steam + game;
    }

    /// <summary>Skinport's fee is taken from the sale price and rounded up to the cent.</summary>
    /// <param name="toEur">Rate from the price currency to EUR, used for the high-value threshold. Null assumes the regular fee.</param>
    public static decimal SkinportFee(decimal price, decimal? toEur)
    {
        if (price <= 0) return 0;
        var rate = toEur is { } r && price * r > SellFees.SkinportHighValueFromEur ? SellFees.SkinportHighValueFee : SellFees.SkinportFee;
        var fee = Math.Ceiling(price * rate * 100m) / 100m;
        return Math.Max(SellFees.SkinportMinimumFee, fee);
    }

    public static SellAdvice Compare(decimal? steamPrice, decimal? skinportPrice, string currency, decimal? toEur)
    {
        var options = new List<SellOption>(2);
        if (steamPrice is { } sp && sp > 0)
        {
            var net = SteamNet(sp);
            options.Add(new SellOption(SellVenue.SteamMarket, "Steam Market", sp, sp - net, net, "Steam Wallet funds"));
        }
        if (skinportPrice is { } kp && kp > 0)
        {
            var fee = SkinportFee(kp, toEur);
            options.Add(new SellOption(SellVenue.Skinport, "Skinport", kp, fee, Math.Max(0, kp - fee), "Bank payout"));
        }
        if (options.Count == 0) return SellAdvice.None(currency);
        var best = options.MaxBy(o => o.Net)!;
        decimal? margin = options.Count > 1 ? best.Net - options.Where(o => o != best).Max(o => o.Net) : null;
        return new SellAdvice(options, best, margin, currency);
    }
}
