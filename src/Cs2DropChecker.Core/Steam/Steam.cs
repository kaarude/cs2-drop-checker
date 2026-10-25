using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cs2DropChecker.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cs2DropChecker.Core.Steam;

public static partial class SteamIdInput
{
    [GeneratedRegex(@"steamcommunity\.com/profiles/(\d{17})", RegexOptions.IgnoreCase)] private static partial Regex ProfileUrl();
    [GeneratedRegex(@"steamcommunity\.com/id/([^/?#\s]+)", RegexOptions.IgnoreCase)] private static partial Regex VanityUrl();
    [GeneratedRegex(@"^\d{17}$")] private static partial Regex Id64();
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{2,32}$")] private static partial Regex VanityName();

    public static bool TryParse(string? input, out ulong id64, out string? vanity)
    {
        id64 = 0;
        vanity = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();

        var m = ProfileUrl().Match(s);
        if (m.Success) return ulong.TryParse(m.Groups[1].Value, out id64) && IsValid(id64);

        m = VanityUrl().Match(s);
        if (m.Success)
        {
            vanity = Uri.UnescapeDataString(m.Groups[1].Value);
            return vanity.Length > 0;
        }

        if (Id64().IsMatch(s)) return ulong.TryParse(s, out id64) && IsValid(id64);
        if (VanityName().IsMatch(s))
        {
            vanity = s;
            return true;
        }
        return false;
    }

    public static bool IsValid(ulong id) => id >= 76561197960265728UL && id < 76561202255233023UL;
}

public sealed class SteamLookupException(string message) : Exception(message);

public sealed class SteamProfileClient(HttpClient http)
{
    public async Task<SteamProfile> ResolveAsync(string input, CancellationToken ct)
    {
        if (!SteamIdInput.TryParse(input, out var id, out var vanity))
            throw new SteamLookupException("That doesn't look like a Steam profile link, SteamID64 or custom name.");

        var url = vanity is not null
            ? $"https://steamcommunity.com/id/{Uri.EscapeDataString(vanity)}/?xml=1"
            : $"https://steamcommunity.com/profiles/{id}/?xml=1";
        string xml;
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                throw new SteamLookupException($"Steam answered {(int)resp.StatusCode}. Try again in a minute.");
            xml = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SteamLookupException("Couldn't reach Steam: " + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SteamLookupException("Steam took too long to answer. Try again.");
        }
        return ParseProfileXml(xml, id);
    }

    public static SteamProfile ParseProfileXml(string xml, ulong fallbackId)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception) { throw new SteamLookupException("Steam returned an unexpected page. Try again in a minute."); }

        var root = doc.Root;
        if (root is null || root.Element("error") is not null)
            throw new SteamLookupException("No Steam profile found with that link, name or ID.");

        if (!ulong.TryParse(root.Element("steamID64")?.Value, out var sid))
        {
            if (fallbackId == 0) throw new SteamLookupException("No Steam profile found with that link, name or ID.");
            sid = fallbackId;
        }
        var name = root.Element("steamID")?.Value.Trim();
        var avatar = root.Element("avatarMedium")?.Value.Trim() ?? "";
        var privacy = root.Element("privacyState")?.Value.Trim() ?? "unknown";
        return new SteamProfile(sid, string.IsNullOrEmpty(name) ? sid.ToString() : name, avatar, privacy);
    }
}

public readonly record struct TransportResponse(int Status, string Body);

/// <summary>Performs GET requests against steamcommunity.com, either anonymously or inside a signed-in browser.</summary>
public interface IInventoryTransport
{
    bool IsSignedIn { get; }
    Task<TransportResponse> GetAsync(string pathAndQuery, CancellationToken ct);
}

public sealed class HttpInventoryTransport(HttpClient http) : IInventoryTransport
{
    public bool IsSignedIn => false;

    public async Task<TransportResponse> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync("https://steamcommunity.com" + pathAndQuery, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new((int)resp.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            return new(-1, ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(-1, "timeout");
        }
    }
}

public sealed class InventoryPage
{
    public List<InventoryItem> Items { get; } = new();
    public bool Success { get; set; }
    public bool MoreItems { get; set; }
    public string? LastAssetId { get; set; }
    public int TotalCount { get; set; }
}

public static partial class InventoryParser
{
    [GeneratedRegex(@"<img", RegexOptions.IgnoreCase)] private static partial Regex ImgTag();
    [GeneratedRegex(@"\d+")] private static partial Regex Digits();

    public static InventoryPage Parse(string json)
    {
        var page = new InventoryPage();
        if (string.IsNullOrWhiteSpace(json)) return page;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return page;

        page.Success = Int(root, "success") == 1;
        page.MoreItems = Int(root, "more_items") == 1;
        page.LastAssetId = Str(root, "last_assetid");
        page.TotalCount = Int(root, "total_inventory_count");

        var descriptions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.TryGetProperty("descriptions", out var descArray) && descArray.ValueKind == JsonValueKind.Array)
            foreach (var d in descArray.EnumerateArray())
                descriptions[Key(Str(d, "classid"), Str(d, "instanceid"))] = d;

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.TryGetProperty("asset_properties", out var propArray) && propArray.ValueKind == JsonValueKind.Array)
            foreach (var p in propArray.EnumerateArray())
                if (Str(p, "assetid") is { } aid && p.TryGetProperty("asset_properties", out var list) && list.ValueKind == JsonValueKind.Array)
                    properties[aid] = list;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var assetId = Str(a, "assetid");
                if (assetId is null) continue;
                if (!descriptions.TryGetValue(Key(Str(a, "classid"), Str(a, "instanceid")), out var d)) continue;
                properties.TryGetValue(assetId, out var props);
                page.Items.Add(Build(a, d, props, assetId));
            }
        return page;
    }

    private static string Key(string? classId, string? instanceId) => $"{classId}_{instanceId ?? "0"}";

    private static InventoryItem Build(JsonElement asset, JsonElement d, JsonElement props, string assetId)
    {
        string typeTag = "", typeName = "", weapon = "", rarityTag = "", rarityName = "", rarityColor = "", quality = "", exterior = "";
        if (d.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var t in tags.EnumerateArray())
            {
                var internalName = Str(t, "internal_name") ?? "";
                var localized = Str(t, "localized_tag_name") ?? "";
                switch (Str(t, "category"))
                {
                    case "Type": typeTag = internalName; typeName = localized; break;
                    case "Weapon": weapon = localized; break;
                    case "Rarity": rarityTag = internalName; rarityName = localized; rarityColor = (Str(t, "color") ?? "").ToLowerInvariant(); break;
                    case "Quality": quality = internalName; break;
                    case "Exterior": exterior = localized; break;
                }
            }

        int stickers = 0, charms = 0;
        int? storage = null;
        if (d.TryGetProperty("descriptions", out var lines) && lines.ValueKind == JsonValueKind.Array)
            foreach (var line in lines.EnumerateArray())
            {
                var name = Str(line, "name");
                var value = Str(line, "value") ?? "";
                if (name == "sticker_info") stickers += ImgTag().Count(value);
                else if (name == "keychain_info") charms += ImgTag().Count(value);
                else if (name == "attr: items count" && Digits().Match(value) is { Success: true } m && int.TryParse(m.Value, out var n)) storage = n;
            }

        double? wear = null;
        int? seed = null;
        string? customName = null;
        if (props.ValueKind == JsonValueKind.Array)
            foreach (var p in props.EnumerateArray())
                switch (Int(p, "propertyid"))
                {
                    case 1:
                        if (int.TryParse(Str(p, "int_value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) seed = s;
                        break;
                    case 2:
                        if (double.TryParse(Str(p, "float_value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) wear = f;
                        break;
                    case 5:
                        customName = Str(p, "string_value");
                        break;
                }

        var displayName = Str(d, "name") ?? "";
        return new InventoryItem
        {
            AssetId = assetId,
            ContextId = Str(asset, "contextid") ?? "2",
            ClassId = Str(asset, "classid") ?? "",
            InstanceId = Str(asset, "instanceid") ?? "0",
            MarketHashName = Str(d, "market_hash_name") ?? displayName,
            Name = displayName,
            Type = Str(d, "type") ?? "",
            IconUrl = Str(d, "icon_url") ?? "",
            TypeTag = typeTag,
            TypeName = typeName,
            Weapon = weapon,
            RarityTag = rarityTag,
            RarityName = rarityName,
            RarityColor = rarityColor,
            QualityTag = quality,
            Exterior = exterior,
            Float = wear,
            PaintSeed = seed,
            CustomName = customName,
            StickerCount = stickers,
            CharmCount = charms,
            Tradable = Int(d, "tradable") == 1,
            Marketable = Int(d, "marketable") == 1,
            StorageCount = storage,
        };
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => null,
            }
            : null;

    internal static int Int(JsonElement e, string name) =>
        int.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

public enum FetchStatus { Ok, Private, NotFound, RateLimited, SignedOut, Failed }

public sealed record InventoryFetchResult(FetchStatus Status, InventorySnapshot? Snapshot, string Message);

public sealed class InventoryFetcher(ILogger<InventoryFetcher>? log = null)
{
    public const string MainContext = "2";
    public const string ProtectedContext = "16";
    private readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;

    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Loads every page of the given contexts. Only context 2 is required; other contexts are best effort
    /// in public mode because Steam may refuse them anonymously.
    /// </summary>
    public async Task<InventoryFetchResult> FetchAsync(IInventoryTransport transport, ulong steamId, IReadOnlyCollection<string> contexts, CancellationToken ct)
    {
        var items = new List<InventoryItem>();
        var totalMain = 0;
        var first = true;
        foreach (var ctx in contexts)
        {
            var required = ctx == MainContext || transport.IsSignedIn;
            string? start = null;
            for (var pageNo = 0; ; pageNo++)
            {
                if (!first) await Task.Delay(PageDelay, ct);
                first = false;
                var path = $"/inventory/{steamId}/730/{ctx}?l=english&count=2000" + (start is null ? "" : $"&start_assetid={start}");
                var r = await transport.GetAsync(path, ct);
                _log.LogDebug("Inventory ctx {Ctx} page {Page}: HTTP {Status}", ctx, pageNo, r.Status);

                if (r.Status == 429) return new(FetchStatus.RateLimited, null, "Steam is rate-limiting inventory requests.");
                if (!required && r.Status != 200) break;
                if (r.Status is 401 or 403)
                    return transport.IsSignedIn
                        ? new(FetchStatus.SignedOut, null, "Steam sign-in expired.")
                        : new(FetchStatus.Private, null, "This inventory is private.");
                if (r.Status == 404) return new(FetchStatus.NotFound, null, "Steam has no CS2 inventory for this profile.");
                if (r.Status != 200) return new(FetchStatus.Failed, null, r.Status < 0 ? "Couldn't reach Steam." : $"Steam answered {r.Status}.");

                InventoryPage page;
                try { page = InventoryParser.Parse(r.Body); }
                catch (JsonException)
                {
                    if (!required) break;
                    return new(FetchStatus.Failed, null, "Steam returned an unexpected response.");
                }
                if (!page.Success)
                {
                    if (!required) break;
                    return new(FetchStatus.Failed, null, "Steam did not return the inventory.");
                }

                if (ctx == MainContext) totalMain = Math.Max(totalMain, page.TotalCount);
                items.AddRange(page.Items);
                if (!page.MoreItems || string.IsNullOrEmpty(page.LastAssetId)) break;
                if (pageNo >= 40) return new(FetchStatus.Failed, null, "Inventory paging did not finish.");
                start = page.LastAssetId;
            }
        }

        var unique = items.GroupBy(i => i.AssetId, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var snapshot = new InventorySnapshot
        {
            SteamId = steamId,
            TakenAt = DateTimeOffset.UtcNow,
            SignedIn = transport.IsSignedIn,
            TotalCount = totalMain,
            Items = unique,
        };
        return new(FetchStatus.Ok, snapshot, $"{unique.Count} items");
    }
}
