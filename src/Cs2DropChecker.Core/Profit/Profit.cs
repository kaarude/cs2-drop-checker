using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;

namespace Cs2DropChecker.Core.Profit;

public static class KeyPrices
{
    /// <summary>
    /// Valve's in-game store price for a case key in USD regions. Keys bought since 2019 can't be traded or sold, so
    /// there is no market price; other regions pay their own store price, which is why the key price is a setting.
    /// </summary>
    public const decimal StoreUsd = 2.49m;

    /// <summary>The configured key price in <paramref name="currency"/>, or the converted store price when none is set.</summary>
    public static decimal? Effective(decimal? configured, string? configuredCurrency, string currency, Func<string, string, decimal?> rate)
    {
        if (configured is { } k && k >= 0)
        {
            if (configuredCurrency is null || configuredCurrency == currency) return k;
            if (rate(configuredCurrency, currency) is { } r) return Math.Round(k * r, 2, MidpointRounding.AwayFromZero);
        }
        return rate("USD", currency) is { } usd ? Math.Round(StoreUsd * usd, 2, MidpointRounding.AwayFromZero) : null;
    }

    /// <summary>Weapon cases need a key; sticker capsules, packages and souvenir packages open for free.</summary>
    public static bool NeedsKey(ArrivalKind kind) => kind == ArrivalKind.CaseOpened;
}

/// <summary>One case or capsule opening: what it cost at the time and what came out.</summary>
public sealed record CaseOpening
{
    public long DropId { get; init; }
    public ulong SteamId { get; init; }
    public DateTimeOffset OpenedAt { get; init; }
    public ArrivalKind Kind { get; init; }
    public string? ContainerName { get; init; }
    public string? ContainerHashName { get; init; }
    public decimal? CasePrice { get; init; }
    public decimal? KeyPrice { get; init; }
    /// <summary>Currency of <see cref="CasePrice"/> and <see cref="KeyPrice"/>.</summary>
    public string? Currency { get; init; }
    public InventoryItem Item { get; init; } = new();
    public decimal? ItemPrice { get; init; }
    public string? ItemCurrency { get; init; }
    /// <summary>Imported from history that predates cost tracking; the case price, if set later, is an estimate.</summary>
    public bool Backfilled { get; init; }

    public bool NeedsKey => KeyPrices.NeedsKey(Kind);
    public bool ContainerKnown => ContainerHashName is { Length: > 0 };

    public decimal? Cost => CasePrice is { } c ? c + (NeedsKey ? KeyPrice ?? 0 : 0) : null;

    public decimal? Net => IsComplete(ItemCurrency ?? "") ? ItemPrice!.Value - Cost!.Value : null;

    /// <summary>True when both sides of the opening are priced in <paramref name="currency"/>.</summary>
    public bool IsComplete(string currency) =>
        ContainerKnown && CasePrice.HasValue && (!NeedsKey || KeyPrice.HasValue) && ItemPrice.HasValue
        && Currency == currency && ItemCurrency == currency;
}

public sealed record ContainerStats(string Name, int Count, decimal Spent, decimal Returned)
{
    public decimal Net => Returned - Spent;
    public decimal? RoiPercent => ProfitCalculator.Roi(Spent, Returned);
}

public sealed record ProfitSummary(
    int Openings,
    int Counted,
    decimal Spent,
    decimal Returned,
    CaseOpening? Best,
    CaseOpening? Worst,
    IReadOnlyList<ContainerStats> ByContainer)
{
    public static readonly ProfitSummary Empty = new(0, 0, 0, 0, null, null, []);

    public decimal Net => Returned - Spent;
    public decimal? RoiPercent => ProfitCalculator.Roi(Spent, Returned);
    /// <summary>Openings that can't be counted yet (case not recorded, price missing, other currency).</summary>
    public int Incomplete => Openings - Counted;
}

public static class ProfitCalculator
{
    /// <summary>Return on the money spent, in percent: (returned − spent) / spent × 100.</summary>
    public static decimal? Roi(decimal spent, decimal returned) =>
        spent <= 0 ? null : Math.Round((returned - spent) / spent * 100m, 1, MidpointRounding.AwayFromZero);

    public static ProfitSummary Summarize(IEnumerable<CaseOpening> openings, string currency, DateTimeOffset? since = null, DateTimeOffset? until = null)
    {
        var all = openings.Where(o => (since is null || o.OpenedAt >= since) && (until is null || o.OpenedAt < until)).ToList();
        var counted = all.Where(o => o.IsComplete(currency)).ToList();
        if (all.Count == 0) return ProfitSummary.Empty;
        var spent = counted.Sum(o => o.Cost!.Value);
        var returned = counted.Sum(o => o.ItemPrice!.Value);
        var best = counted.MaxBy(o => o.Net!.Value);
        var worst = counted.MinBy(o => o.Net!.Value);
        var groups = counted
            .GroupBy(o => o.ContainerName ?? o.ContainerHashName ?? "")
            .Select(g => new ContainerStats(g.Key, g.Count(), g.Sum(o => o.Cost!.Value), g.Sum(o => o.ItemPrice!.Value)))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ProfitSummary(all.Count, counted.Count, spent, returned, best, worst, groups);
    }

    /// <summary>"You're down £47.20", "You're up €3.10" or "You're even".</summary>
    public static string Headline(decimal net, string currency) =>
        net < 0 ? $"You're down {Currencies.Format(-net, currency)}"
        : net > 0 ? $"You're up {Currencies.Format(net, currency)}"
        : "You're even";

    public static string Signed(decimal value, string currency) =>
        value > 0 ? "+" + Currencies.Format(value, currency) : value < 0 ? "-" + Currencies.Format(-value, currency) : Currencies.Format(0, currency);
}
