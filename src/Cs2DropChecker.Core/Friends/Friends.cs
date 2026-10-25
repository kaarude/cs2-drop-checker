using System.Net;
using System.Text.RegularExpressions;
using Cs2DropChecker.Core.Detection;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Profit;
using Cs2DropChecker.Core.Weekly;

namespace Cs2DropChecker.Core.Friends;

public enum FriendSource { Manual, Steam }

public static class FriendStatus
{
    public const string Pending = "pending";
    public const string Ok = "ok";
    public const string Private = "private";
    public const string NotFound = "notfound";
    public const string Failed = "error";

    public static string Label(string status) => status switch
    {
        Ok => "Public",
        Private => "Private",
        NotFound => "No CS2 inventory",
        Failed => "Couldn't check",
        _ => "Not checked yet",
    };
}

public sealed record Friend
{
    public ulong SteamId { get; init; }
    public string Name { get; init; } = "";
    public string AvatarUrl { get; init; } = "";
    public FriendSource Source { get; init; }
    public bool Tracked { get; init; } = true;
    public DateTimeOffset AddedAt { get; init; }
    public string Status { get; init; } = FriendStatus.Pending;
    public DateTimeOffset? CheckedAt { get; init; }
    public decimal? Value { get; init; }
    public string? Currency { get; init; }
    public int? ItemCount { get; init; }
}

/// <summary>A new item in a friend's public inventory, explained the same way as the user's own drops.</summary>
public sealed record FriendArrival
{
    public long Id { get; init; }
    public ulong FriendId { get; init; }
    public DateTimeOffset SeenAt { get; init; }
    public ArrivalKind Kind { get; init; }
    public InventoryItem Item { get; init; } = new();
    public decimal? Price { get; init; }
    public string? Currency { get; init; }
    public string? ContainerHashName { get; init; }
    public decimal? CasePrice { get; init; }
    public decimal? KeyPrice { get; init; }

    /// <summary>Estimated case result: item value minus case and key, when everything is priced.</summary>
    public decimal? CaseNet =>
        ArrivalKindText.IsOpening(Kind) && Price is { } p && CasePrice is { } c && (!KeyPrices.NeedsKey(Kind) || KeyPrice.HasValue)
            ? p - c - (KeyPrices.NeedsKey(Kind) ? KeyPrice!.Value : 0)
            : null;
}

public sealed record SteamFriend(ulong SteamId, string Name, string AvatarUrl);

/// <summary>Reads the friend blocks on steamcommunity.com/my/friends/ (signed-in view).</summary>
public static partial class FriendsListParser
{
    [GeneratedRegex(@"<div[^>]+class=""[^""]*friend_block_v2[^""]*""[^>]*data-steamid=""(\d{17})""", RegexOptions.IgnoreCase)] private static partial Regex Block();
    [GeneratedRegex(@"<img[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase)] private static partial Regex Img();
    [GeneratedRegex(@"class=""friend_block_content""[^>]*>\s*([^<]+)", RegexOptions.IgnoreCase)] private static partial Regex Name();

    public static IReadOnlyList<SteamFriend> Parse(string html)
    {
        var list = new List<SteamFriend>();
        if (string.IsNullOrEmpty(html)) return list;
        var blocks = Block().Matches(html);
        var seen = new HashSet<ulong>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var m = blocks[i];
            if (!ulong.TryParse(m.Groups[1].Value, out var id) || !seen.Add(id)) continue;
            var end = i + 1 < blocks.Count ? blocks[i + 1].Index : html.Length;
            var chunk = html[m.Index..end];
            var name = Name().Match(chunk) is { Success: true } n ? WebUtility.HtmlDecode(n.Groups[1].Value).Trim() : "";
            var avatar = Img().Match(chunk) is { Success: true } a ? WebUtility.HtmlDecode(a.Groups[1].Value) : "";
            list.Add(new SteamFriend(id, name.Length > 0 ? name : id.ToString(), avatar));
        }
        return list;
    }
}

public static class FriendTracker
{
    /// <summary>Items that arrived between two public snapshots of a friend's inventory. The first snapshot is only a baseline.</summary>
    public static IReadOnlyList<FriendArrival> Arrivals(
        ulong friendId,
        InventorySnapshot? previous,
        InventorySnapshot next,
        Func<string, PriceQuote?> price,
        decimal? keyPrice,
        string currency)
    {
        if (previous is null) return [];
        var diff = DropClassifier.Classify(previous, next, cs2Active: false);
        var list = new List<FriendArrival>();
        foreach (var a in diff.Added.Where(a => ArrivalKindText.ShowsCard(a.Kind)))
        {
            var q = price(a.Item.MarketHashName);
            var container = ArrivalKindText.IsOpening(a.Kind) ? a.Source : null;
            var cq = container is null ? null : price(container.MarketHashName);
            list.Add(new FriendArrival
            {
                FriendId = friendId,
                SeenAt = next.TakenAt,
                Kind = a.Kind,
                Item = a.Item,
                Price = q?.Currency == currency ? q.Price : null,
                Currency = q is null ? null : currency,
                ContainerHashName = container?.MarketHashName,
                CasePrice = cq?.Currency == currency ? cq.Price : null,
                KeyPrice = container is not null && KeyPrices.NeedsKey(a.Kind) ? keyPrice : null,
            });
        }
        return list;
    }
}

public sealed record LeaderboardRow
{
    public ulong SteamId { get; init; }
    public string Name { get; init; } = "";
    public string AvatarUrl { get; init; } = "";
    public bool IsYou { get; init; }
    public string Status { get; init; } = FriendStatus.Ok;
    public string? BestName { get; init; }
    public string? BestRarityHex { get; init; }
    public decimal? BestValue { get; init; }
    public decimal WeekValue { get; init; }
    public int WeekCount { get; init; }
    public decimal? InventoryValue { get; init; }
    public decimal? CaseNet { get; init; }
    /// <summary>True for friends: openings are inferred from public inventory changes and priced at the time they were seen.</summary>
    public bool CaseNetEstimated { get; init; }
    public int CaseCount { get; init; }

    public bool IsPrivate => Status == FriendStatus.Private;
}

public static class Leaderboard
{
    public static LeaderboardRow ForYou(ulong id, string name, string avatar, IEnumerable<DropRecord> drops, decimal? inventoryValue,
        ProfitSummary lifetime, string currency, DateTimeOffset now)
    {
        var start = Cs2Week.Start(now);
        var week = drops.Where(d => d.SeenAt >= start && ArrivalKindText.ShowsCard(d.Kind)).ToList();
        var priced = week.Where(d => d.PriceAtDrop.HasValue && d.Currency == currency).ToList();
        var best = priced.MaxBy(d => d.PriceAtDrop!.Value);
        return new LeaderboardRow
        {
            SteamId = id,
            Name = name,
            AvatarUrl = avatar,
            IsYou = true,
            BestName = best?.Item.Name,
            BestRarityHex = best?.Item.RarityHex,
            BestValue = best?.PriceAtDrop,
            WeekValue = priced.Sum(d => d.PriceAtDrop!.Value),
            WeekCount = week.Count,
            InventoryValue = inventoryValue,
            CaseNet = lifetime.Counted > 0 ? lifetime.Net : null,
            CaseCount = lifetime.Counted,
        };
    }

    public static LeaderboardRow ForFriend(Friend f, IEnumerable<FriendArrival> arrivals, string currency, DateTimeOffset now)
    {
        var start = Cs2Week.Start(now);
        var all = arrivals.Where(a => a.Currency is null || a.Currency == currency).ToList();
        var week = all.Where(a => a.SeenAt >= start).ToList();
        var priced = week.Where(a => a.Price.HasValue).ToList();
        var best = priced.MaxBy(a => a.Price!.Value);
        var openings = all.Where(a => a.CaseNet.HasValue).ToList();
        return new LeaderboardRow
        {
            SteamId = f.SteamId,
            Name = f.Name,
            AvatarUrl = f.AvatarUrl,
            Status = f.Status,
            BestName = best?.Item.Name,
            BestRarityHex = best?.Item.RarityHex,
            BestValue = best?.Price,
            WeekValue = priced.Sum(a => a.Price!.Value),
            WeekCount = week.Count,
            InventoryValue = f.Currency == currency ? f.Value : null,
            CaseNet = openings.Count > 0 ? openings.Sum(a => a.CaseNet!.Value) : null,
            CaseNetEstimated = true,
            CaseCount = openings.Count,
        };
    }

    /// <summary>Highest weekly drop value first; private inventories last.</summary>
    public static IReadOnlyList<LeaderboardRow> Rank(IEnumerable<LeaderboardRow> rows) =>
        rows.OrderBy(r => r.IsPrivate).ThenByDescending(r => r.WeekValue).ThenByDescending(r => r.InventoryValue ?? 0).ToList();
}
