using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Steam;

namespace Cs2DropChecker.Core.Market;

public enum SellState { Sellable, Protected, Listed, NotMarketable }

/// <param name="Until">When the item can be listed. <paramref name="Estimated"/> is true when it comes from first-seen + 7 days.</param>
public readonly record struct Sellability(SellState State, DateTimeOffset? Until, bool Estimated)
{
    public bool CanSell => State == SellState.Sellable;
}

public static class SellRules
{
    public static readonly TimeSpan ProtectionPeriod = TimeSpan.FromDays(7);

    /// <summary>
    /// Items received by trade or from the market sit in context 16 for seven days and can't be listed. Since March 2026
    /// listed items stay in the inventory, so the caller passes whether the asset is currently listed.
    /// </summary>
    public static Sellability Evaluate(InventoryItem item, bool listed, DateTimeOffset? firstSeen, DateTimeOffset now)
    {
        if (listed) return new(SellState.Listed, null, false);
        if (item.ContextId == InventoryFetcher.ProtectedContext)
        {
            if (item.ProtectedUntil is { } exact) return new(SellState.Protected, exact, false);
            return new(SellState.Protected, firstSeen is { } seen ? seen + ProtectionPeriod : null, true);
        }
        if (!item.Marketable)
            return item.ProtectedUntil is { } until && until > now
                ? new(SellState.Protected, until, false)
                : new(SellState.NotMarketable, null, false);
        return new(SellState.Sellable, null, false);
    }

    private static bool Ready(InventoryItem i) => i.ContextId == InventoryFetcher.MainContext && i.Marketable;

    /// <summary>
    /// Items that could not be listed in <paramref name="previous"/> and can be now. Protection ending usually moves the
    /// item from context 16 to context 2 under a new asset id, so the fingerprint links the two.
    /// </summary>
    public static List<InventoryItem> NewlySellable(InventorySnapshot previous, InventorySnapshot next)
    {
        if (!previous.SignedIn || !next.SignedIn || previous.SteamId != next.SteamId) return [];
        var before = previous.Items.GroupBy(i => i.AssetId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // How many not-yet-sellable copies of each item left that state between the snapshots.
        var released = previous.Items.Where(i => !Ready(i)).GroupBy(i => i.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var i in next.Items.Where(i => !Ready(i)))
            if (released.ContainsKey(i.Fingerprint)) released[i.Fingerprint]--;

        var result = new List<InventoryItem>();
        var ready = next.Items.Where(Ready).ToList();
        foreach (var item in ready)
        {
            if (!before.TryGetValue(item.AssetId, out var old) || Ready(old)) continue;
            result.Add(item);
            released[item.Fingerprint] = released.GetValueOrDefault(item.Fingerprint) - 1;
        }
        foreach (var item in ready)
        {
            if (before.ContainsKey(item.AssetId)) continue;
            if (released.TryGetValue(item.Fingerprint, out var n) && n > 0)
            {
                released[item.Fingerprint] = n - 1;
                result.Add(item);
            }
        }
        return result;
    }
}
