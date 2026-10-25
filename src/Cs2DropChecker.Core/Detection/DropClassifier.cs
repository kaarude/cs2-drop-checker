using Cs2DropChecker.Core.Models;

namespace Cs2DropChecker.Core.Detection;

public sealed record ClassifiedItem(InventoryItem Item, ArrivalKind Kind, bool SeenLate);

public sealed record DiffResult(IReadOnlyList<ClassifiedItem> Added, IReadOnlyList<InventoryItem> Removed, int NewlyHidden);

/// <summary>
/// Explains why items appeared between two complete snapshots. Removed items are used as evidence:
/// a consumed container means a case opening, 5–10 consumed skins mean a trade-up, and so on.
/// </summary>
public static class DropClassifier
{
    public static DiffResult Classify(InventorySnapshot previous, InventorySnapshot next, bool cs2Active)
    {
        var prevIds = previous.Items.Select(i => i.AssetId).ToHashSet(StringComparer.Ordinal);
        var nextIds = next.Items.Select(i => i.AssetId).ToHashSet(StringComparer.Ordinal);

        // When the view changes between signed-in and public, other contexts appear or vanish wholesale,
        // so only context 2 is compared.
        var modeSwitch = previous.SignedIn != next.SignedIn;
        var removedAll = previous.Items.Where(i => !nextIds.Contains(i.AssetId)).ToList();
        var removed = modeSwitch ? removedAll.Where(i => i.ContextId == "2").ToList() : removedAll;
        var open = next.Items.Where(i => !prevIds.Contains(i.AssetId) && (!modeSwitch || i.ContextId == "2")).ToList();

        var result = new List<ClassifiedItem>();
        var moveCandidates = new List<InventoryItem>(removedAll);
        var evidence = new List<InventoryItem>(removed);

        foreach (var added in open.ToList())
        {
            var match = moveCandidates.FirstOrDefault(r => SameItem(r, added));
            if (match is null) continue;
            moveCandidates.Remove(match);
            evidence.Remove(match);
            open.Remove(added);
            result.Add(new(added, ArrivalKind.Moved, false));
        }

        var takenFromStorage = StorageTakenOut(previous, next);
        if (takenFromStorage > 0)
            foreach (var added in open.Where(i => !i.IsStorageUnit).Take(takenFromStorage).ToList())
            {
                open.Remove(added);
                result.Add(new(added, ArrivalKind.FromStorage, false));
            }

        var consumedSkins = evidence.Where(i => i.HasWear && !i.IsStar && !i.IsContainer).ToList();
        if (consumedSkins.Count >= 5)
        {
            var inputTier = consumedSkins.GroupBy(Tier).OrderByDescending(g => g.Count()).First().Key;
            var outcomes = open.Where(i => (i.HasWear || i.IsStar) && Tier(i) > inputTier)
                .Take(Math.Max(1, consumedSkins.Count / 5)).ToList();
            foreach (var added in outcomes)
            {
                open.Remove(added);
                result.Add(new(added, ArrivalKind.TradeUp, false));
            }
            if (outcomes.Count > 0) evidence.RemoveAll(consumedSkins.Contains);
        }

        foreach (var container in evidence.Where(i => i.IsContainer && !i.IsStorageUnit).ToList())
        {
            var kind = ContainerKind(container);
            var added = open.FirstOrDefault(i => !i.IsContainer && Expected(kind, i)) ?? open.FirstOrDefault(i => !i.IsContainer);
            if (added is null) break;
            open.Remove(added);
            evidence.Remove(container);
            result.Add(new(added, kind, false));
        }

        if (!previous.SignedIn && !next.SignedIn)
        {
            var unhidden = Math.Max(0, previous.HiddenCount - next.HiddenCount);
            foreach (var added in open.Where(i => i.ContextId == "2").OrderByDescending(i => i.Tradable).Take(unhidden).ToList())
            {
                open.Remove(added);
                result.Add(new(added, ArrivalKind.LatePurchase, true));
            }
        }

        foreach (var added in open.Where(i => i.ContextId != "2").ToList())
        {
            open.Remove(added);
            result.Add(new(added, ArrivalKind.Purchase, false));
        }

        foreach (var added in open)
            result.Add(new(added, cs2Active ? ArrivalKind.WeeklyDrop : ArrivalKind.NewItem, false));

        var newlyHidden = !previous.SignedIn && !next.SignedIn ? Math.Max(0, next.HiddenCount - previous.HiddenCount) : 0;
        return new DiffResult(result, removed, newlyHidden);
    }

    private static bool SameItem(InventoryItem a, InventoryItem b)
    {
        if (a.Fingerprint == b.Fingerprint) return true;
        return a.Float.HasValue && b.Float.HasValue && a.Float == b.Float && a.PaintSeed == b.PaintSeed;
    }

    internal static int Tier(InventoryItem item)
    {
        if (item.IsStar) return 6;
        var r = item.RarityTag;
        if (r.Contains("Ancient", StringComparison.Ordinal)) return 5;
        if (r.Contains("Legendary", StringComparison.Ordinal)) return 4;
        if (r.Contains("Mythical", StringComparison.Ordinal)) return 3;
        if (r.StartsWith("Rarity_Rare", StringComparison.Ordinal)) return 2;
        if (r.StartsWith("Rarity_Uncommon", StringComparison.Ordinal)) return 1;
        return 0;
    }

    private static int StorageTakenOut(InventorySnapshot previous, InventorySnapshot next)
    {
        var before = previous.Items.Where(i => i.StorageCount.HasValue).ToDictionary(i => i.AssetId, i => i.StorageCount!.Value, StringComparer.Ordinal);
        var taken = 0;
        foreach (var unit in next.Items.Where(i => i.StorageCount.HasValue))
            if (before.TryGetValue(unit.AssetId, out var count) && count > unit.StorageCount!.Value)
                taken += count - unit.StorageCount.Value;
        return taken;
    }

    private static ArrivalKind ContainerKind(InventoryItem container)
    {
        var n = container.Name;
        if (n.Contains("Souvenir", StringComparison.OrdinalIgnoreCase)) return ArrivalKind.SouvenirPackage;
        if (n.Contains("Capsule", StringComparison.OrdinalIgnoreCase) || n.Contains("Package", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Box", StringComparison.OrdinalIgnoreCase) || n.Contains("Pack", StringComparison.OrdinalIgnoreCase))
            return ArrivalKind.CapsuleOpened;
        return ArrivalKind.CaseOpened;
    }

    private static bool Expected(ArrivalKind kind, InventoryItem item) => kind switch
    {
        ArrivalKind.SouvenirPackage => item.IsSouvenir,
        ArrivalKind.CapsuleOpened => !item.HasWear,
        _ => item.HasWear || item.IsStar,
    };
}
