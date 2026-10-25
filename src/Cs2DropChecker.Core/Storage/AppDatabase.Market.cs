using System.Text.Json;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Util;
using Microsoft.Data.Sqlite;

namespace Cs2DropChecker.Core.Storage;

public enum ListingState { Active, Pending, Gone }

public sealed record ListingRow
{
    public string ListingId { get; init; } = "";
    public ulong SteamId { get; init; }
    public string AssetId { get; init; } = "";
    public string MarketHashName { get; init; } = "";
    public string Name { get; init; } = "";
    public long BuyerCents { get; init; }
    public long ReceiveCents { get; init; }
    public string Currency { get; init; } = "";
    public DateTimeOffset FirstSeen { get; init; }
    public ListingState State { get; init; }
    public DateTimeOffset? GoneAt { get; init; }
    public InventoryItem? Item { get; init; }
}

public sealed record SaleRecord
{
    public long Id { get; init; }
    public ulong SteamId { get; init; }
    public string ListingId { get; init; } = "";
    public string AssetId { get; init; } = "";
    public string MarketHashName { get; init; } = "";
    public DateTimeOffset SoldAt { get; init; }
    public DateTimeOffset ListedAt { get; init; }
    public long BuyerCents { get; init; }
    public long ReceiveCents { get; init; }
    public string Currency { get; init; } = "";
    public decimal? ArrivalPrice { get; init; }
    public string? ArrivalCurrency { get; init; }
    public ArrivalKind? ArrivalKind { get; init; }
    public InventoryItem Item { get; init; } = new();

    public decimal Receive => ReceiveCents / 100m;
    public decimal BuyerPays => BuyerCents / 100m;
}

public sealed partial class AppDatabase
{
    public void UpsertListing(ListingRow l)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO market_listing(listing_id,steam_id,asset_id,market_hash_name,name,buyer_cents,receive_cents,currency,first_seen,state,gone_at,item_json)
            VALUES ($id,$sid,$aid,$hash,$name,$buyer,$recv,$cur,$first,$state,$gone,$json)
            ON CONFLICT(listing_id) DO UPDATE SET asset_id=excluded.asset_id, market_hash_name=excluded.market_hash_name, name=excluded.name,
                buyer_cents=excluded.buyer_cents, receive_cents=excluded.receive_cents, currency=excluded.currency, state=excluded.state,
                gone_at=excluded.gone_at, item_json=COALESCE(excluded.item_json, market_listing.item_json)
            """;
        cmd.Parameters.AddWithValue("$id", l.ListingId);
        cmd.Parameters.AddWithValue("$sid", l.SteamId.ToString());
        cmd.Parameters.AddWithValue("$aid", l.AssetId);
        cmd.Parameters.AddWithValue("$hash", l.MarketHashName);
        cmd.Parameters.AddWithValue("$name", l.Name);
        cmd.Parameters.AddWithValue("$buyer", l.BuyerCents);
        cmd.Parameters.AddWithValue("$recv", l.ReceiveCents);
        cmd.Parameters.AddWithValue("$cur", l.Currency);
        cmd.Parameters.AddWithValue("$first", Ms(l.FirstSeen));
        cmd.Parameters.AddWithValue("$state", l.State.ToString());
        cmd.Parameters.AddWithValue("$gone", l.GoneAt is { } g ? Ms(g) : DBNull.Value);
        cmd.Parameters.AddWithValue("$json", l.Item is null ? DBNull.Value : JsonSerializer.Serialize(l.Item, JsonDefaults.Options));
        cmd.ExecuteNonQuery();
    }

    public List<ListingRow> GetListings(ulong steamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT listing_id,asset_id,market_hash_name,name,buyer_cents,receive_cents,currency,first_seen,state,gone_at,item_json
            FROM market_listing WHERE steam_id=$sid ORDER BY first_seen DESC
            """;
        cmd.Parameters.AddWithValue("$sid", steamId.ToString());
        var list = new List<ListingRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ListingRow
            {
                ListingId = r.GetString(0),
                SteamId = steamId,
                AssetId = r.GetString(1),
                MarketHashName = r.GetString(2),
                Name = r.GetString(3),
                BuyerCents = r.GetInt64(4),
                ReceiveCents = r.GetInt64(5),
                Currency = r.GetString(6),
                FirstSeen = FromMs(r.GetInt64(7)),
                State = Enum.TryParse<ListingState>(r.GetString(8), out var st) ? st : ListingState.Active,
                GoneAt = r.IsDBNull(9) ? null : FromMs(r.GetInt64(9)),
                Item = r.IsDBNull(10) ? null : JsonSerializer.Deserialize<InventoryItem>(r.GetString(10), JsonDefaults.Options),
            });
        return list;
    }

    public void MarkListingGone(string listingId, DateTimeOffset at)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE market_listing SET state='Gone', gone_at=$at WHERE listing_id=$id AND state <> 'Gone'";
        cmd.Parameters.AddWithValue("$id", listingId);
        cmd.Parameters.AddWithValue("$at", Ms(at));
        cmd.ExecuteNonQuery();
    }

    public void DeleteListing(string listingId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM market_listing WHERE listing_id=$id";
        cmd.Parameters.AddWithValue("$id", listingId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the new row id, or 0 when this listing was already recorded as sold.</summary>
    public long AddSale(SaleRecord s)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO market_sale(steam_id,listing_id,asset_id,market_hash_name,sold_at,listed_at,buyer_cents,receive_cents,currency,arrival_price,arrival_currency,arrival_kind,item_json)
            VALUES ($sid,$lid,$aid,$hash,$sold,$listed,$buyer,$recv,$cur,$ap,$ac,$ak,$json)
            """;
        cmd.Parameters.AddWithValue("$sid", s.SteamId.ToString());
        cmd.Parameters.AddWithValue("$lid", s.ListingId);
        cmd.Parameters.AddWithValue("$aid", s.AssetId);
        cmd.Parameters.AddWithValue("$hash", s.MarketHashName);
        cmd.Parameters.AddWithValue("$sold", Ms(s.SoldAt));
        cmd.Parameters.AddWithValue("$listed", Ms(s.ListedAt));
        cmd.Parameters.AddWithValue("$buyer", s.BuyerCents);
        cmd.Parameters.AddWithValue("$recv", s.ReceiveCents);
        cmd.Parameters.AddWithValue("$cur", s.Currency);
        cmd.Parameters.AddWithValue("$ap", s.ArrivalPrice is { } p ? (double)p : DBNull.Value);
        cmd.Parameters.AddWithValue("$ac", (object?)s.ArrivalCurrency ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ak", s.ArrivalKind is { } k ? k.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(s.Item, JsonDefaults.Options));
        if (cmd.ExecuteNonQuery() == 0) return 0;
        using var id = c.CreateCommand();
        id.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(id.ExecuteScalar());
    }

    public List<SaleRecord> GetSales(ulong steamId, int limit, DateTimeOffset? since = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,listing_id,asset_id,market_hash_name,sold_at,listed_at,buyer_cents,receive_cents,currency,arrival_price,arrival_currency,arrival_kind,item_json
            FROM market_sale WHERE steam_id=$sid AND sold_at >= $since ORDER BY sold_at DESC, id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$sid", steamId.ToString());
        cmd.Parameters.AddWithValue("$since", since is { } s ? Ms(s) : 0L);
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<SaleRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SaleRecord
            {
                Id = r.GetInt64(0),
                SteamId = steamId,
                ListingId = r.GetString(1),
                AssetId = r.GetString(2),
                MarketHashName = r.GetString(3),
                SoldAt = FromMs(r.GetInt64(4)),
                ListedAt = FromMs(r.GetInt64(5)),
                BuyerCents = r.GetInt64(6),
                ReceiveCents = r.GetInt64(7),
                Currency = r.GetString(8),
                ArrivalPrice = r.IsDBNull(9) ? null : Money(r.GetDouble(9)),
                ArrivalCurrency = r.IsDBNull(10) ? null : r.GetString(10),
                ArrivalKind = !r.IsDBNull(11) && Enum.TryParse<ArrivalKind>(r.GetString(11), out var k) ? k : null,
                Item = JsonSerializer.Deserialize<InventoryItem>(r.GetString(12), JsonDefaults.Options) ?? new InventoryItem(),
            });
        return list;
    }

    /// <summary>
    /// The arrival record for an item. Asset ids change when protection ends, so this falls back to the newest
    /// record of the same item by fingerprint.
    /// </summary>
    public DropRecord? FindArrival(ulong steamId, InventoryItem item)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, seen_at, kind, seen_late, price, currency, price_source, item_json FROM drop_record
            WHERE steam_id=$sid AND (asset_id=$aid OR market_hash_name=$hash) ORDER BY (asset_id=$aid) DESC, seen_at DESC LIMIT 50
            """;
        cmd.Parameters.AddWithValue("$sid", steamId.ToString());
        cmd.Parameters.AddWithValue("$aid", item.AssetId);
        cmd.Parameters.AddWithValue("$hash", item.MarketHashName);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var stored = JsonSerializer.Deserialize<InventoryItem>(r.GetString(7), JsonDefaults.Options) ?? new InventoryItem();
            if (stored.AssetId != item.AssetId && stored.Fingerprint != item.Fingerprint) continue;
            return new DropRecord
            {
                Id = r.GetInt64(0),
                SteamId = steamId,
                SeenAt = FromMs(r.GetInt64(1)),
                Kind = Enum.TryParse<ArrivalKind>(r.GetString(2), out var k) ? k : ArrivalKind.NewItem,
                SeenLate = r.GetInt64(3) == 1,
                PriceAtDrop = r.IsDBNull(4) ? null : Money(r.GetDouble(4)),
                Currency = r.IsDBNull(5) ? null : r.GetString(5),
                PriceSource = r.IsDBNull(6) ? null : r.GetString(6),
                Item = stored,
            };
        }
        return null;
    }

    /// <summary>Remembers when each asset was first seen, so protection end can be estimated when Steam doesn't say.</summary>
    public void TouchFirstSeen(ulong steamId, IEnumerable<string> assetIds, DateTimeOffset at)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO asset_first_seen(steam_id,asset_id,first_seen) VALUES ($sid,$aid,$at)";
            var aid = cmd.Parameters.Add("$aid", SqliteType.Text);
            cmd.Parameters.AddWithValue("$sid", steamId.ToString());
            cmd.Parameters.AddWithValue("$at", Ms(at));
            foreach (var id in assetIds)
            {
                aid.Value = id;
                cmd.ExecuteNonQuery();
            }
        }
        using (var prune = c.CreateCommand())
        {
            prune.Transaction = tx;
            prune.CommandText = "DELETE FROM asset_first_seen WHERE steam_id=$sid AND first_seen < $old";
            prune.Parameters.AddWithValue("$sid", steamId.ToString());
            prune.Parameters.AddWithValue("$old", Ms(at - TimeSpan.FromDays(60)));
            prune.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public Dictionary<string, DateTimeOffset> GetFirstSeen(ulong steamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT asset_id, first_seen FROM asset_first_seen WHERE steam_id=$sid";
        cmd.Parameters.AddWithValue("$sid", steamId.ToString());
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = FromMs(r.GetInt64(1));
        return map;
    }
}
