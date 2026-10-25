using System.Text.Json;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Util;
using Microsoft.Data.Sqlite;

namespace Cs2DropChecker.Core.Storage;

public sealed record ValuePoint(DateTimeOffset At, decimal Total, string Currency, int PricedCount, int ItemCount);

public sealed partial class AppDatabase
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public AppDatabase(string file)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = true }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        using (var wal = c.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS inventory_snapshot(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    steam_id TEXT NOT NULL,
                    taken_at INTEGER NOT NULL,
                    signed_in INTEGER NOT NULL,
                    total_count INTEGER NOT NULL,
                    item_count INTEGER NOT NULL,
                    items_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_snapshot_sid ON inventory_snapshot(steam_id, taken_at);
                CREATE TABLE IF NOT EXISTS drop_record(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    steam_id TEXT NOT NULL,
                    asset_id TEXT NOT NULL,
                    seen_at INTEGER NOT NULL,
                    kind TEXT NOT NULL,
                    seen_late INTEGER NOT NULL,
                    market_hash_name TEXT NOT NULL,
                    price REAL,
                    currency TEXT,
                    price_source TEXT,
                    item_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_drop_sid ON drop_record(steam_id, seen_at);
                CREATE UNIQUE INDEX IF NOT EXISTS ux_drop_asset ON drop_record(steam_id, asset_id);
                CREATE TABLE IF NOT EXISTS price_cache(
                    market_hash_name TEXT NOT NULL,
                    source TEXT NOT NULL,
                    currency TEXT NOT NULL,
                    price REAL NOT NULL,
                    fetched_at INTEGER NOT NULL,
                    PRIMARY KEY(market_hash_name, source, currency));
                CREATE TABLE IF NOT EXISTS value_history(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    steam_id TEXT NOT NULL,
                    taken_at INTEGER NOT NULL,
                    total REAL NOT NULL,
                    currency TEXT NOT NULL,
                    priced_count INTEGER NOT NULL,
                    item_count INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_value_sid ON value_history(steam_id, taken_at);
                CREATE TABLE IF NOT EXISTS poll_log(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    at INTEGER NOT NULL,
                    mode TEXT NOT NULL,
                    status TEXT NOT NULL,
                    items INTEGER NOT NULL,
                    message TEXT);
                CREATE TABLE IF NOT EXISTS market_listing(
                    listing_id TEXT PRIMARY KEY,
                    steam_id TEXT NOT NULL,
                    asset_id TEXT NOT NULL,
                    market_hash_name TEXT NOT NULL,
                    name TEXT NOT NULL,
                    buyer_cents INTEGER NOT NULL,
                    receive_cents INTEGER NOT NULL,
                    currency TEXT NOT NULL,
                    first_seen INTEGER NOT NULL,
                    state TEXT NOT NULL,
                    gone_at INTEGER,
                    item_json TEXT);
                CREATE INDEX IF NOT EXISTS ix_listing_sid ON market_listing(steam_id);
                CREATE TABLE IF NOT EXISTS market_sale(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    steam_id TEXT NOT NULL,
                    listing_id TEXT NOT NULL,
                    asset_id TEXT NOT NULL,
                    market_hash_name TEXT NOT NULL,
                    sold_at INTEGER NOT NULL,
                    listed_at INTEGER NOT NULL,
                    buyer_cents INTEGER NOT NULL,
                    receive_cents INTEGER NOT NULL,
                    currency TEXT NOT NULL,
                    arrival_price REAL,
                    arrival_currency TEXT,
                    arrival_kind TEXT,
                    item_json TEXT NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS ux_sale_listing ON market_sale(listing_id);
                CREATE INDEX IF NOT EXISTS ix_sale_sid ON market_sale(steam_id, sold_at);
                CREATE TABLE IF NOT EXISTS asset_first_seen(
                    steam_id TEXT NOT NULL,
                    asset_id TEXT NOT NULL,
                    first_seen INTEGER NOT NULL,
                    PRIMARY KEY(steam_id, asset_id));
                """;
            cmd.ExecuteNonQuery();
        }
        using var v = c.CreateCommand();
        v.CommandText = "SELECT COUNT(*) FROM schema_version";
        if (Convert.ToInt64(v.ExecuteScalar()) == 0)
        {
            v.CommandText = $"INSERT INTO schema_version(version) VALUES ({SchemaVersion})";
            v.ExecuteNonQuery();
        }
        Migrate(c);
    }

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    private static DateTimeOffset FromMs(long v) => DateTimeOffset.FromUnixTimeMilliseconds(v);
    private static decimal Money(double v) => Math.Round((decimal)v, 2);

    public void SaveSnapshot(InventorySnapshot s)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO inventory_snapshot(steam_id,taken_at,signed_in,total_count,item_count,items_json) VALUES ($sid,$at,$si,$tc,$ic,$json)";
            cmd.Parameters.AddWithValue("$sid", s.SteamId.ToString());
            cmd.Parameters.AddWithValue("$at", Ms(s.TakenAt));
            cmd.Parameters.AddWithValue("$si", s.SignedIn ? 1 : 0);
            cmd.Parameters.AddWithValue("$tc", s.TotalCount);
            cmd.Parameters.AddWithValue("$ic", s.Items.Count);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(s.Items, JsonDefaults.Options));
            cmd.ExecuteNonQuery();
        }
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM inventory_snapshot WHERE steam_id=$sid AND id NOT IN (SELECT id FROM inventory_snapshot WHERE steam_id=$sid ORDER BY taken_at DESC LIMIT 3)";
            del.Parameters.AddWithValue("$sid", s.SteamId.ToString());
            del.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public InventorySnapshot? LoadLatestSnapshot(ulong steamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT taken_at, signed_in, total_count, items_json FROM inventory_snapshot WHERE steam_id=$sid ORDER BY taken_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$sid", steamId.ToString());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new InventorySnapshot
        {
            SteamId = steamId,
            TakenAt = FromMs(r.GetInt64(0)),
            SignedIn = r.GetInt64(1) == 1,
            TotalCount = (int)r.GetInt64(2),
            Items = JsonSerializer.Deserialize<List<InventoryItem>>(r.GetString(3), JsonDefaults.Options) ?? new(),
        };
    }

    /// <summary>Returns the new row id, or 0 when this asset was already recorded.</summary>
    public long AddDrop(DropRecord d)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO drop_record(steam_id,asset_id,seen_at,kind,seen_late,market_hash_name,price,currency,price_source,item_json)
            VALUES ($sid,$aid,$at,$kind,$late,$name,$price,$cur,$src,$json)
            """;
        cmd.Parameters.AddWithValue("$sid", d.SteamId.ToString());
        cmd.Parameters.AddWithValue("$aid", d.Item.AssetId);
        cmd.Parameters.AddWithValue("$at", Ms(d.SeenAt));
        cmd.Parameters.AddWithValue("$kind", d.Kind.ToString());
        cmd.Parameters.AddWithValue("$late", d.SeenLate ? 1 : 0);
        cmd.Parameters.AddWithValue("$name", d.Item.MarketHashName);
        cmd.Parameters.AddWithValue("$price", d.PriceAtDrop is { } p ? (object)(double)p : DBNull.Value);
        cmd.Parameters.AddWithValue("$cur", (object?)d.Currency ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$src", (object?)d.PriceSource ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(d.Item, JsonDefaults.Options));
        if (cmd.ExecuteNonQuery() == 0) return 0;
        using var id = c.CreateCommand();
        id.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(id.ExecuteScalar());
    }

    public void UpdateDropPrice(long id, decimal price, string currency, string source)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE drop_record SET price=$p, currency=$c, price_source=$s WHERE id=$id";
        cmd.Parameters.AddWithValue("$p", (double)price);
        cmd.Parameters.AddWithValue("$c", currency);
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<DropRecord> GetDrops(ulong steamId, int limit, int offset = 0, DateTimeOffset? since = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, seen_at, kind, seen_late, price, currency, price_source, item_json FROM drop_record
            WHERE steam_id=$sid AND seen_at >= $since ORDER BY seen_at DESC, id DESC LIMIT $limit OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$sid", steamId.ToString());
        cmd.Parameters.AddWithValue("$since", since is { } s ? Ms(s) : 0L);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        var list = new List<DropRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DropRecord
            {
                Id = r.GetInt64(0),
                SteamId = steamId,
                SeenAt = FromMs(r.GetInt64(1)),
                Kind = Enum.TryParse<ArrivalKind>(r.GetString(2), out var k) ? k : ArrivalKind.NewItem,
                SeenLate = r.GetInt64(3) == 1,
                PriceAtDrop = r.IsDBNull(4) ? null : Money(r.GetDouble(4)),
                Currency = r.IsDBNull(5) ? null : r.GetString(5),
                PriceSource = r.IsDBNull(6) ? null : r.GetString(6),
                Item = JsonSerializer.Deserialize<InventoryItem>(r.GetString(7), JsonDefaults.Options) ?? new InventoryItem(),
            });
        }
        return list;
    }

    public int CountDrops(ulong steamId, DateTimeOffset? since = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM drop_record WHERE steam_id=$sid AND seen_at >= $since";
        cmd.Parameters.AddWithValue("$sid", steamId.ToString());
        cmd.Parameters.AddWithValue("$since", since is { } s ? Ms(s) : 0L);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void AddValuePoint(ulong steamId, DateTimeOffset at, decimal total, string currency, int priced, int count)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO value_history(steam_id,taken_at,total,currency,priced_count,item_count) VALUES ($sid,$at,$t,$c,$p,$n)";
        cmd.Parameters.AddWithValue("$sid", steamId.ToString());
        cmd.Parameters.AddWithValue("$at", Ms(at));
        cmd.Parameters.AddWithValue("$t", (double)total);
        cmd.Parameters.AddWithValue("$c", currency);
        cmd.Parameters.AddWithValue("$p", priced);
        cmd.Parameters.AddWithValue("$n", count);
        cmd.ExecuteNonQuery();
    }

    public List<ValuePoint> GetValueHistory(ulong steamId, DateTimeOffset since, string currency)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT taken_at,total,currency,priced_count,item_count FROM value_history WHERE steam_id=$sid AND taken_at >= $since AND currency=$c ORDER BY taken_at";
        cmd.Parameters.AddWithValue("$sid", steamId.ToString());
        cmd.Parameters.AddWithValue("$since", Ms(since));
        cmd.Parameters.AddWithValue("$c", currency);
        var list = new List<ValuePoint>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ValuePoint(FromMs(r.GetInt64(0)), Money(r.GetDouble(1)), r.GetString(2), (int)r.GetInt64(3), (int)r.GetInt64(4)));
        return list;
    }

    public ValuePoint? GetLastValuePoint(ulong steamId, string currency, DateTimeOffset? before = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT taken_at,total,currency,priced_count,item_count FROM value_history WHERE steam_id=$sid AND currency=$c AND taken_at < $before ORDER BY taken_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$sid", steamId.ToString());
        cmd.Parameters.AddWithValue("$c", currency);
        cmd.Parameters.AddWithValue("$before", before is { } b ? Ms(b) : long.MaxValue);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new ValuePoint(FromMs(r.GetInt64(0)), Money(r.GetDouble(1)), r.GetString(2), (int)r.GetInt64(3), (int)r.GetInt64(4)) : null;
    }

    public void UpsertPrice(string marketHashName, string source, string currency, decimal price, DateTimeOffset at)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_cache(market_hash_name,source,currency,price,fetched_at) VALUES ($n,$s,$c,$p,$at)
            ON CONFLICT(market_hash_name,source,currency) DO UPDATE SET price=excluded.price, fetched_at=excluded.fetched_at
            """;
        cmd.Parameters.AddWithValue("$n", marketHashName);
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$c", currency);
        cmd.Parameters.AddWithValue("$p", (double)price);
        cmd.Parameters.AddWithValue("$at", Ms(at));
        cmd.ExecuteNonQuery();
    }

    public PriceQuote? GetCachedPrice(string marketHashName, string source, string currency)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT price, fetched_at FROM price_cache WHERE market_hash_name=$n AND source=$s AND currency=$c";
        cmd.Parameters.AddWithValue("$n", marketHashName);
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$c", currency);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new PriceQuote(Money(r.GetDouble(0)), currency, source, FromMs(r.GetInt64(1))) : null;
    }

    public List<(string Name, PriceQuote Quote)> GetCachedPrices(string source, string currency)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT market_hash_name, price, fetched_at FROM price_cache WHERE source=$s AND currency=$c";
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$c", currency);
        var list = new List<(string, PriceQuote)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), new PriceQuote(Money(r.GetDouble(1)), currency, source, FromMs(r.GetInt64(2)))));
        return list;
    }

    public void AddPollLog(string mode, string status, int items, string? message)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO poll_log(at,mode,status,items,message) VALUES ($at,$m,$s,$i,$msg);
            DELETE FROM poll_log WHERE id <= (SELECT MAX(id) - 2000 FROM poll_log);
            """;
        cmd.Parameters.AddWithValue("$at", Ms(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$m", mode);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$i", items);
        cmd.Parameters.AddWithValue("$msg", (object?)message ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
