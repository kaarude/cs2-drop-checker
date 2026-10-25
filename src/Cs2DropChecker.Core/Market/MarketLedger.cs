using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Storage;

namespace Cs2DropChecker.Core.Market;

/// <summary>
/// Tracks the user's market listings and turns vanished ones into sales. Listed CS2 items stay in the inventory,
/// so a listing that disappears is a sale only if its item is also gone from a snapshot taken afterwards.
/// </summary>
public sealed class MarketLedger(AppDatabase db)
{
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(14);

    public void ApplyListings(ulong steamId, IReadOnlyList<MarketListing> listings, string currency, InventorySnapshot? inventory, DateTimeOffset now)
    {
        var existing = db.GetListings(steamId).ToDictionary(l => l.ListingId, StringComparer.Ordinal);
        var items = inventory?.SteamId == steamId
            ? inventory.Items.GroupBy(i => i.AssetId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
            : new Dictionary<string, InventoryItem>(StringComparer.Ordinal);
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in listings)
        {
            current.Add(l.ListingId);
            existing.TryGetValue(l.ListingId, out var old);
            items.TryGetValue(l.AssetId, out var item);
            db.UpsertListing(new ListingRow
            {
                ListingId = l.ListingId,
                SteamId = steamId,
                AssetId = l.AssetId,
                MarketHashName = l.MarketHashName.Length > 0 ? l.MarketHashName : item?.MarketHashName ?? old?.MarketHashName ?? l.Name,
                Name = l.Name.Length > 0 ? l.Name : item?.Name ?? old?.Name ?? "",
                BuyerCents = l.BuyerPays ?? old?.BuyerCents ?? 0,
                ReceiveCents = l.Receive ?? old?.ReceiveCents ?? 0,
                Currency = currency,
                FirstSeen = old?.FirstSeen ?? now,
                State = l.AwaitingConfirmation ? ListingState.Pending : ListingState.Active,
                GoneAt = null,
                Item = item ?? old?.Item,
            });
        }
        foreach (var old in existing.Values)
            if (old.State != ListingState.Gone && !current.Contains(old.ListingId))
                db.MarkListingGone(old.ListingId, now);
    }

    /// <summary>Call when the user cancels a listing, so it is never mistaken for a sale.</summary>
    public void Forget(string listingId) => db.DeleteListing(listingId);

    public List<SaleRecord> Resolve(ulong steamId, InventorySnapshot snapshot)
    {
        var sales = new List<SaleRecord>();
        if (snapshot.SteamId != steamId) return sales;
        var ids = snapshot.Items.Select(i => i.AssetId).ToHashSet(StringComparer.Ordinal);
        foreach (var row in db.GetListings(steamId))
        {
            if (row.State != ListingState.Gone || row.GoneAt is not { } gone) continue;
            if (snapshot.TakenAt - gone > GiveUpAfter)
            {
                db.DeleteListing(row.ListingId);
                continue;
            }
            if (snapshot.TakenAt <= gone) continue;
            if (ids.Contains(row.AssetId))
            {
                db.DeleteListing(row.ListingId);
                continue;
            }
            var item = row.Item ?? new InventoryItem { AssetId = row.AssetId, MarketHashName = row.MarketHashName, Name = row.Name };
            var arrival = db.FindArrival(steamId, item);
            var sale = new SaleRecord
            {
                SteamId = steamId,
                ListingId = row.ListingId,
                AssetId = row.AssetId,
                MarketHashName = row.MarketHashName,
                SoldAt = gone,
                ListedAt = row.FirstSeen,
                BuyerCents = row.BuyerCents,
                ReceiveCents = row.ReceiveCents,
                Currency = row.Currency,
                ArrivalPrice = arrival?.PriceAtDrop,
                ArrivalCurrency = arrival?.Currency,
                ArrivalKind = arrival?.Kind,
                Item = item,
            };
            var id = db.AddSale(sale);
            db.DeleteListing(row.ListingId);
            if (id > 0) sales.Add(sale with { Id = id });
        }
        return sales;
    }

    /// <summary>Asset ids that are listed or were listed recently; their disappearance is a sale, not a case opening.</summary>
    public HashSet<string> ListedAssets(ulong steamId) =>
        db.GetListings(steamId).Select(l => l.AssetId).ToHashSet(StringComparer.Ordinal);
}
