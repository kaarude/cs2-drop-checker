using System.Text.Json;
using Cs2DropChecker.Core.Alerts;
using Cs2DropChecker.Core.Friends;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Profit;
using Cs2DropChecker.Core.Util;
using Microsoft.Data.Sqlite;

namespace Cs2DropChecker.Core.Storage;

/// <summary>Schema migrations plus storage for case openings, friends and price alerts.</summary>
public sealed partial class AppDatabase
{
    public const int LatestSchemaVersion = 2;

    public int CurrentSchemaVersion
    {
        get
        {
            using var c = Open();
            return ReadVersion(c);
        }
    }

    private static int ReadVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_version";
        return Convert.ToInt32(cmd.ExecuteScalar() is long v ? v : 1);
    }

    /// <summary>
    /// Brings an older database up to <see cref="LatestSchemaVersion"/>. A database that already holds history is first copied
    /// to data.db.v{old}.bak, and each step runs in one transaction, so a failed step leaves the old data untouched.
    /// </summary>
    private void Migrate(SqliteConnection c)
    {
        var version = ReadVersion(c);
        if (version >= LatestSchemaVersion) return;
        BackupBeforeMigration(c, version);
        if (version < 2) Step(c, 2, MigrationV2);
    }

    private void BackupBeforeMigration(SqliteConnection c, int version)
    {
        var file = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        if (string.IsNullOrEmpty(file) || file == ":memory:") return;
        using (var any = c.CreateCommand())
        {
            any.CommandText = "SELECT EXISTS(SELECT 1 FROM drop_record) OR EXISTS(SELECT 1 FROM value_history)";
            if (Convert.ToInt64(any.ExecuteScalar()) == 0) return;
        }
        var backup = $"{file}.v{version}.bak";
        if (File.Exists(backup)) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", backup);
        cmd.ExecuteNonQuery();
    }

    private static void Step(SqliteConnection c, int to, string sql)
    {
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        using (var v = c.CreateCommand())
        {
            v.Transaction = tx;
            v.CommandText = "UPDATE schema_version SET version=$v";
            v.Parameters.AddWithValue("$v", to);
            v.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // Openings recorded before cost tracking are backfilled without a container: the old rows never stored which case was consumed.
    private const string MigrationV2 = """
        CREATE TABLE IF NOT EXISTS case_opening(
            drop_id INTEGER PRIMARY KEY,
            steam_id TEXT NOT NULL,
            opened_at INTEGER NOT NULL,
            kind TEXT NOT NULL,
            container_name TEXT,
            container_hash TEXT,
            case_price REAL,
            key_price REAL,
            currency TEXT,
            backfilled INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS ix_opening_sid ON case_opening(steam_id, opened_at);
        INSERT OR IGNORE INTO case_opening(drop_id, steam_id, opened_at, kind, backfilled)
            SELECT id, steam_id, seen_at, kind, 1 FROM drop_record WHERE kind IN ('CaseOpened','CapsuleOpened','SouvenirPackage');

        CREATE TABLE IF NOT EXISTS friend(
            steam_id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            avatar_url TEXT NOT NULL,
            source TEXT NOT NULL,
            tracked INTEGER NOT NULL,
            added_at INTEGER NOT NULL,
            status TEXT NOT NULL,
            checked_at INTEGER,
            value REAL,
            currency TEXT,
            item_count INTEGER);
        CREATE TABLE IF NOT EXISTS friend_snapshot(
            steam_id TEXT PRIMARY KEY,
            taken_at INTEGER NOT NULL,
            items_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS friend_arrival(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            friend_id TEXT NOT NULL,
            asset_id TEXT NOT NULL,
            seen_at INTEGER NOT NULL,
            kind TEXT NOT NULL,
            market_hash_name TEXT NOT NULL,
            price REAL,
            currency TEXT,
            container_hash TEXT,
            case_price REAL,
            key_price REAL,
            item_json TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_farrival ON friend_arrival(friend_id, asset_id);
        CREATE INDEX IF NOT EXISTS ix_farrival_fid ON friend_arrival(friend_id, seen_at);

        CREATE TABLE IF NOT EXISTS price_alert(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL,
            market_hash_name TEXT,
            item_name TEXT,
            icon_url TEXT,
            percent REAL,
            target REAL,
            base REAL,
            currency TEXT NOT NULL,
            armed INTEGER NOT NULL DEFAULT 1,
            enabled INTEGER NOT NULL DEFAULT 1,
            created_at INTEGER NOT NULL,
            fired_at INTEGER);
        """;

    private static object Db(decimal? v) => v is { } d ? (double)d : DBNull.Value;
    private static object Db(string? v) => (object?)v ?? DBNull.Value;
    private static decimal? MoneyOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Money(r.GetDouble(i));
    private static string? StrOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ---- Case openings ----

    public void AddCaseOpening(CaseOpening o)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO case_opening(drop_id,steam_id,opened_at,kind,container_name,container_hash,case_price,key_price,currency,backfilled)
            VALUES ($id,$sid,$at,$kind,$cn,$ch,$cp,$kp,$cur,$bf)
            """;
        cmd.Parameters.AddWithValue("$id", o.DropId);
        cmd.Parameters.AddWithValue("$sid", o.SteamId.ToString());
        cmd.Parameters.AddWithValue("$at", Ms(o.OpenedAt));
        cmd.Parameters.AddWithValue("$kind", o.Kind.ToString());
        cmd.Parameters.AddWithValue("$cn", Db(o.ContainerName));
        cmd.Parameters.AddWithValue("$ch", Db(o.ContainerHashName));
        cmd.Parameters.AddWithValue("$cp", Db(o.CasePrice));
        cmd.Parameters.AddWithValue("$kp", Db(o.KeyPrice));
        cmd.Parameters.AddWithValue("$cur", Db(o.Currency));
        cmd.Parameters.AddWithValue("$bf", o.Backfilled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Sets the cost side of an opening: after a late case price, or when the user names the case of a backfilled row.</summary>
    public void UpdateOpeningCost(long dropId, string? containerName, string? containerHash, decimal? casePrice, decimal? keyPrice, string currency)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE case_opening SET container_name=COALESCE($cn, container_name), container_hash=COALESCE($ch, container_hash),
                case_price=$cp, key_price=$kp, currency=$cur WHERE drop_id=$id
            """;
        cmd.Parameters.AddWithValue("$id", dropId);
        cmd.Parameters.AddWithValue("$cn", Db(containerName));
        cmd.Parameters.AddWithValue("$ch", Db(containerHash));
        cmd.Parameters.AddWithValue("$cp", Db(casePrice));
        cmd.Parameters.AddWithValue("$kp", Db(keyPrice));
        cmd.Parameters.AddWithValue("$cur", currency);
        cmd.ExecuteNonQuery();
    }

    public List<CaseOpening> GetOpenings(ulong steamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT o.drop_id, o.opened_at, o.kind, o.container_name, o.container_hash, o.case_price, o.key_price, o.currency, o.backfilled,
                   d.price, d.currency, d.item_json
            FROM case_opening o JOIN drop_record d ON d.id = o.drop_id
            WHERE o.steam_id=$sid ORDER BY o.opened_at DESC, o.drop_id DESC
            """;
        cmd.Parameters.AddWithValue("$sid", steamId.ToString());
        var list = new List<CaseOpening>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new CaseOpening
            {
                DropId = r.GetInt64(0),
                SteamId = steamId,
                OpenedAt = FromMs(r.GetInt64(1)),
                Kind = Enum.TryParse<ArrivalKind>(r.GetString(2), out var k) ? k : ArrivalKind.CaseOpened,
                ContainerName = StrOrNull(r, 3),
                ContainerHashName = StrOrNull(r, 4),
                CasePrice = MoneyOrNull(r, 5),
                KeyPrice = MoneyOrNull(r, 6),
                Currency = StrOrNull(r, 7),
                Backfilled = r.GetInt64(8) == 1,
                ItemPrice = MoneyOrNull(r, 9),
                ItemCurrency = StrOrNull(r, 10),
                Item = JsonSerializer.Deserialize<InventoryItem>(r.GetString(11), JsonDefaults.Options) ?? new InventoryItem(),
            });
        return list;
    }

    // ---- Friends ----

    public void UpsertFriend(Friend f)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO friend(steam_id,name,avatar_url,source,tracked,added_at,status) VALUES ($id,$n,$a,$src,$t,$at,$st)
            ON CONFLICT(steam_id) DO UPDATE SET name=excluded.name, avatar_url=excluded.avatar_url, tracked=excluded.tracked,
                source=CASE WHEN friend.source='Manual' THEN friend.source ELSE excluded.source END
            """;
        cmd.Parameters.AddWithValue("$id", f.SteamId.ToString());
        cmd.Parameters.AddWithValue("$n", f.Name);
        cmd.Parameters.AddWithValue("$a", f.AvatarUrl);
        cmd.Parameters.AddWithValue("$src", f.Source.ToString());
        cmd.Parameters.AddWithValue("$t", f.Tracked ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", Ms(f.AddedAt));
        cmd.Parameters.AddWithValue("$st", f.Status);
        cmd.ExecuteNonQuery();
    }

    public List<Friend> GetFriends()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT steam_id,name,avatar_url,source,tracked,added_at,status,checked_at,value,currency,item_count FROM friend ORDER BY name COLLATE NOCASE";
        var list = new List<Friend>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Friend
            {
                SteamId = ulong.Parse(r.GetString(0)),
                Name = r.GetString(1),
                AvatarUrl = r.GetString(2),
                Source = Enum.TryParse<FriendSource>(r.GetString(3), out var s) ? s : FriendSource.Manual,
                Tracked = r.GetInt64(4) == 1,
                AddedAt = FromMs(r.GetInt64(5)),
                Status = r.GetString(6),
                CheckedAt = r.IsDBNull(7) ? null : FromMs(r.GetInt64(7)),
                Value = MoneyOrNull(r, 8),
                Currency = StrOrNull(r, 9),
                ItemCount = r.IsDBNull(10) ? null : (int)r.GetInt64(10),
            });
        return list;
    }

    public void SetFriendTracked(ulong id, bool tracked)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE friend SET tracked=$t WHERE steam_id=$id";
        cmd.Parameters.AddWithValue("$t", tracked ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public void UpdateFriendStatus(ulong id, string status, DateTimeOffset at, decimal? value, string? currency, int? items)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE friend SET status=$st, checked_at=$at, value=COALESCE($v, value), currency=COALESCE($c, currency), item_count=COALESCE($n, item_count)
            WHERE steam_id=$id
            """;
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$at", Ms(at));
        cmd.Parameters.AddWithValue("$v", Db(value));
        cmd.Parameters.AddWithValue("$c", Db(currency));
        cmd.Parameters.AddWithValue("$n", items is { } n ? n : DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public void RemoveFriend(ulong id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var sql in (string[])["DELETE FROM friend WHERE steam_id=$id", "DELETE FROM friend_snapshot WHERE steam_id=$id", "DELETE FROM friend_arrival WHERE friend_id=$id"])
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void SaveFriendSnapshot(InventorySnapshot s)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO friend_snapshot(steam_id,taken_at,items_json) VALUES ($id,$at,$json)
            ON CONFLICT(steam_id) DO UPDATE SET taken_at=excluded.taken_at, items_json=excluded.items_json
            """;
        cmd.Parameters.AddWithValue("$id", s.SteamId.ToString());
        cmd.Parameters.AddWithValue("$at", Ms(s.TakenAt));
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(s.Items, JsonDefaults.Options));
        cmd.ExecuteNonQuery();
    }

    public InventorySnapshot? LoadFriendSnapshot(ulong id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT taken_at, items_json FROM friend_snapshot WHERE steam_id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var items = JsonSerializer.Deserialize<List<InventoryItem>>(r.GetString(1), JsonDefaults.Options) ?? new();
        return new InventorySnapshot { SteamId = id, TakenAt = FromMs(r.GetInt64(0)), SignedIn = false, TotalCount = items.Count, Items = items };
    }

    public void AddFriendArrival(FriendArrival a)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO friend_arrival(friend_id,asset_id,seen_at,kind,market_hash_name,price,currency,container_hash,case_price,key_price,item_json)
            VALUES ($fid,$aid,$at,$kind,$name,$p,$cur,$ch,$cp,$kp,$json)
            """;
        cmd.Parameters.AddWithValue("$fid", a.FriendId.ToString());
        cmd.Parameters.AddWithValue("$aid", a.Item.AssetId);
        cmd.Parameters.AddWithValue("$at", Ms(a.SeenAt));
        cmd.Parameters.AddWithValue("$kind", a.Kind.ToString());
        cmd.Parameters.AddWithValue("$name", a.Item.MarketHashName);
        cmd.Parameters.AddWithValue("$p", Db(a.Price));
        cmd.Parameters.AddWithValue("$cur", Db(a.Currency));
        cmd.Parameters.AddWithValue("$ch", Db(a.ContainerHashName));
        cmd.Parameters.AddWithValue("$cp", Db(a.CasePrice));
        cmd.Parameters.AddWithValue("$kp", Db(a.KeyPrice));
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(a.Item, JsonDefaults.Options));
        cmd.ExecuteNonQuery();
    }

    public List<FriendArrival> GetFriendArrivals(ulong friendId, DateTimeOffset? since = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,seen_at,kind,price,currency,container_hash,case_price,key_price,item_json FROM friend_arrival
            WHERE friend_id=$fid AND seen_at >= $since ORDER BY seen_at DESC
            """;
        cmd.Parameters.AddWithValue("$fid", friendId.ToString());
        cmd.Parameters.AddWithValue("$since", since is { } s ? Ms(s) : 0L);
        var list = new List<FriendArrival>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new FriendArrival
            {
                Id = r.GetInt64(0),
                FriendId = friendId,
                SeenAt = FromMs(r.GetInt64(1)),
                Kind = Enum.TryParse<ArrivalKind>(r.GetString(2), out var k) ? k : ArrivalKind.NewItem,
                Price = MoneyOrNull(r, 3),
                Currency = StrOrNull(r, 4),
                ContainerHashName = StrOrNull(r, 5),
                CasePrice = MoneyOrNull(r, 6),
                KeyPrice = MoneyOrNull(r, 7),
                Item = JsonSerializer.Deserialize<InventoryItem>(r.GetString(8), JsonDefaults.Options) ?? new InventoryItem(),
            });
        return list;
    }

    // ---- Price alerts ----

    public long AddAlert(AlertRule a)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_alert(kind,market_hash_name,item_name,icon_url,percent,target,base,currency,armed,enabled,created_at)
            VALUES ($k,$n,$in,$icon,$p,$t,$b,$c,1,1,$at);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$k", a.Kind.ToString());
        cmd.Parameters.AddWithValue("$n", Db(a.MarketHashName));
        cmd.Parameters.AddWithValue("$in", Db(a.ItemName));
        cmd.Parameters.AddWithValue("$icon", Db(a.IconUrl));
        cmd.Parameters.AddWithValue("$p", Db(a.Percent));
        cmd.Parameters.AddWithValue("$t", Db(a.Target));
        cmd.Parameters.AddWithValue("$b", Db(a.Base));
        cmd.Parameters.AddWithValue("$c", a.Currency);
        cmd.Parameters.AddWithValue("$at", Ms(a.CreatedAt == default ? DateTimeOffset.UtcNow : a.CreatedAt));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public List<AlertRule> GetAlerts()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,kind,market_hash_name,item_name,icon_url,percent,target,base,currency,armed,enabled,created_at,fired_at FROM price_alert ORDER BY id";
        var list = new List<AlertRule>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!Enum.TryParse<AlertKind>(r.GetString(1), out var kind)) continue;
            list.Add(new AlertRule
            {
                Id = r.GetInt64(0),
                Kind = kind,
                MarketHashName = StrOrNull(r, 2),
                ItemName = StrOrNull(r, 3),
                IconUrl = StrOrNull(r, 4),
                Percent = MoneyOrNull(r, 5),
                Target = MoneyOrNull(r, 6),
                Base = MoneyOrNull(r, 7),
                Currency = r.GetString(8),
                Armed = r.GetInt64(9) == 1,
                Enabled = r.GetInt64(10) == 1,
                CreatedAt = FromMs(r.GetInt64(11)),
                FiredAt = r.IsDBNull(12) ? null : FromMs(r.GetInt64(12)),
            });
        }
        return list;
    }

    public void UpdateAlertState(long id, bool armed, DateTimeOffset? firedAt)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE price_alert SET armed=$a, fired_at=COALESCE($f, fired_at) WHERE id=$id";
        cmd.Parameters.AddWithValue("$a", armed ? 1 : 0);
        cmd.Parameters.AddWithValue("$f", firedAt is { } f ? Ms(f) : DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Re-arms an alert; for percentage moves the new base is the current price.</summary>
    public void ResetAlert(long id, decimal? newBase)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE price_alert SET armed=1, base=COALESCE($b, base) WHERE id=$id";
        cmd.Parameters.AddWithValue("$b", Db(newBase));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetAlertEnabled(long id, bool enabled)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE price_alert SET enabled=$e WHERE id=$id";
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteAlert(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM price_alert WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
