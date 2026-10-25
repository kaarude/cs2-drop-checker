using Cs2DropChecker.Core.Models;

namespace Cs2DropChecker.Core.Valuation;

public sealed record ValueGroup(string Name, string ColorHex, decimal Value, int Count);

public sealed record InventoryValue(
    decimal Total,
    int PricedCount,
    int ItemCount,
    IReadOnlyList<ValueGroup> Groups,
    InventoryItem? MostValuable,
    decimal MostValuablePrice)
{
    public static readonly InventoryValue Empty = new(0, 0, 0, InventoryValuation.EmptyGroups(), null, 0);
}

public static class InventoryValuation
{
    public static readonly (string Name, string Color)[] GroupDefs =
    [
        ("Knives & gloves", "e4ae39"),
        ("Covert", "eb4b4b"),
        ("Classified", "d32ce6"),
        ("Restricted", "8847ff"),
        ("Mil-Spec", "4b69ff"),
        ("Lower grades & other", "b0c3d9"),
    ];

    public static int GroupIndex(InventoryItem item)
    {
        if (item.IsStar) return 0;
        var r = item.RarityTag;
        if (r.Contains("Ancient", StringComparison.Ordinal)) return 1;
        if (r.Contains("Legendary", StringComparison.Ordinal)) return 2;
        if (r.Contains("Mythical", StringComparison.Ordinal)) return 3;
        if (r.StartsWith("Rarity_Rare", StringComparison.Ordinal)) return 4;
        return 5;
    }

    public static IReadOnlyList<ValueGroup> EmptyGroups() => GroupDefs.Select(g => new ValueGroup(g.Name, g.Color, 0, 0)).ToList();

    public static InventoryValue Compute(IEnumerable<InventoryItem> items, Func<InventoryItem, decimal?> price)
    {
        var values = new decimal[GroupDefs.Length];
        var counts = new int[GroupDefs.Length];
        decimal total = 0, best = 0;
        int priced = 0, count = 0;
        InventoryItem? bestItem = null;
        foreach (var item in items)
        {
            count++;
            var g = GroupIndex(item);
            counts[g]++;
            if (price(item) is not { } p) continue;
            priced++;
            total += p;
            values[g] += p;
            if (p > best)
            {
                best = p;
                bestItem = item;
            }
        }
        var groups = GroupDefs.Select((d, i) => new ValueGroup(d.Name, d.Color, values[i], counts[i])).ToList();
        return new InventoryValue(total, priced, count, groups, bestItem, best);
    }
}
