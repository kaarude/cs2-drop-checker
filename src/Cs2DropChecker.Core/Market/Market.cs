using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Selling;

namespace Cs2DropChecker.Core.Market;

/// <summary>Steam's g_rgWalletInfo. Amounts are in hundredths of the wallet currency.</summary>
public sealed record WalletInfo
{
    public int CurrencyCode { get; init; } = 3;
    public string Country { get; init; } = "";
    public long Balance { get; init; }
    public long DelayedBalance { get; init; }
    public long MaxBalance { get; init; }
    public bool HasFee { get; init; } = true;
    public decimal FeePercent { get; init; } = SellFees.SteamTransactionFee;
    public long FeeMinimum { get; init; } = MarketFees.ToCents(SellFees.SteamMinimumFee);
    public long FeeBase { get; init; }
    public decimal PublisherFeePercent { get; init; } = SellFees.SteamGameFee;

    public CurrencyInfo Currency => Currencies.FromSteamCode(CurrencyCode);
    public string CurrencyIso => Currency.Code;
}

public readonly record struct MarketFee(long BuyerPays, long Receive, long SteamFee, long PublisherFee)
{
    public bool IsValid => Receive >= 1;
}

/// <summary>Port of CalculateFeeAmount / CalculateAmountToSendForDesiredReceivedAmount from Steam's economy_common.js.</summary>
public static class MarketFees
{
    public static MarketFee ForReceive(long receive, WalletInfo w)
    {
        if (receive < 0) receive = 0;
        if (!w.HasFee) return new(receive, receive, 0, 0);
        var steam = (long)Math.Floor(Math.Max(receive * w.FeePercent, w.FeeMinimum) + w.FeeBase);
        var publisher = w.PublisherFeePercent > 0 ? (long)Math.Floor(Math.Max(receive * w.PublisherFeePercent, 1m)) : 0;
        return new(receive + steam + publisher, receive, steam, publisher);
    }

    public static MarketFee ForBuyerPrice(long buyerPays, WalletInfo w)
    {
        if (buyerPays < 0) buyerPays = 0;
        if (!w.HasFee) return new(buyerPays, buyerPays, 0, 0);
        var estimate = (long)((buyerPays - w.FeeBase) / (w.FeePercent + w.PublisherFeePercent + 1));
        var undershot = false;
        var fees = ForReceive(estimate, w);
        for (var i = 0; fees.BuyerPays != buyerPays && i < 10; i++)
        {
            if (fees.BuyerPays > buyerPays)
            {
                if (undershot)
                {
                    fees = ForReceive(estimate - 1, w);
                    return fees with { SteamFee = fees.SteamFee + (buyerPays - fees.BuyerPays), BuyerPays = buyerPays };
                }
                estimate--;
            }
            else
            {
                undershot = true;
                estimate++;
            }
            fees = ForReceive(estimate, w);
        }
        return fees;
    }

    public static long ToCents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    public static decimal FromCents(long cents) => cents / 100m;
}

public readonly record struct OrderLevel(long Price, long Quantity);

/// <summary>Market order book. Prices are what the buyer pays, in hundredths of <see cref="CurrencyCode"/>.</summary>
public sealed record OrderBook(int CurrencyCode, long? LowestSell, long? HighestBuy, long SellCount, long BuyCount, IReadOnlyList<OrderLevel> Sells, IReadOnlyList<OrderLevel> Buys);

public sealed record PricePoint(DateTimeOffset At, decimal Price, int Volume);

public sealed record MarketListing
{
    public string ListingId { get; init; } = "";
    public string AssetId { get; init; } = "";
    public string ContextId { get; init; } = "2";
    public string Name { get; init; } = "";
    public string MarketHashName { get; init; } = "";
    public long? BuyerPays { get; init; }
    public long? Receive { get; init; }
    public string ListedOn { get; init; } = "";
    public bool AwaitingConfirmation { get; init; }
}

public sealed record MarketHome(WalletInfo? Wallet, string? SessionId, ulong SteamId, IReadOnlyList<MarketListing> Listings, int ActiveCount);

public sealed record ListingsPage(bool Success, int TotalCount, IReadOnlyList<MarketListing> Listings);

public sealed record SellResult(bool Success, bool NeedsMobileConfirmation, bool NeedsEmailConfirmation, string? EmailDomain, string Message);

public static partial class MarketParsers
{
    [GeneratedRegex(@"g_rgWalletInfo\s*=\s*\{")] private static partial Regex WalletStart();
    [GeneratedRegex(@"g_sessionID\s*=\s*""([^""]+)""")] private static partial Regex SessionId();
    [GeneratedRegex(@"g_steamID\s*=\s*""(\d{17})""")] private static partial Regex PageSteamId();
    [GeneratedRegex(@"my_market_header_active""[^>]*>\s*([^<]*)<")] private static partial Regex SectionHeader();
    [GeneratedRegex(@"id=""mylisting_(\d+)""")] private static partial Regex ListingRow();
    [GeneratedRegex(@"\w+\(\s*'mylisting'\s*,\s*'(\d+)'\s*,\s*(\d+)\s*,\s*'(\d+)'\s*,\s*'(\d+)'")] private static partial Regex ListingAction();
    [GeneratedRegex(@"CreateItemHoverFromContainer\(\s*\w+\s*,\s*'mylisting_(\d+)_\w+'\s*,\s*(\d+)\s*,\s*'(\d+)'\s*,\s*'(\d+)'")] private static partial Regex Hover();
    [GeneratedRegex(@"title=""This is the price the buyer pays\.""[^>]*>\s*([^<]+?)\s*<")] private static partial Regex BuyerPrice();
    [GeneratedRegex(@"title=""This is how much you will receive\.""[^>]*>\s*\(?\s*([^<)]+?)\s*\)?\s*<")] private static partial Regex ReceivePrice();
    [GeneratedRegex(@"class=""market_listing_item_name""[^>]*>(?:\s*<a[^>]*>)?\s*([^<]+?)\s*<")] private static partial Regex ItemName();
    [GeneratedRegex(@"market/listings/730/([^""'?#<\s]+)")] private static partial Regex ListingLink();
    [GeneratedRegex(@"market_listing_listed_date can_combine""[^>]*>\s*([^<]+?)\s*<")] private static partial Regex ListedDate();
    [GeneratedRegex(@"^\s*([A-Z][a-z]{2})\s+(\d{1,2})\s+(\d{4})\s+(\d{1,2})")] private static partial Regex HistoryDate();

    public static WalletInfo? ParseWallet(string html)
    {
        var m = WalletStart().Match(html);
        if (!m.Success) return null;
        var json = ExtractObject(html, m.Index + m.Length - 1);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var w = doc.RootElement;
            if (w.ValueKind != JsonValueKind.Object) return null;
            var currency = Long(w, "wallet_currency");
            if (currency <= 0) return null;
            return new WalletInfo
            {
                CurrencyCode = (int)currency,
                Country = Str(w, "wallet_country") ?? "",
                Balance = Long(w, "wallet_balance"),
                DelayedBalance = Long(w, "wallet_delayed_balance"),
                MaxBalance = Long(w, "wallet_max_balance"),
                HasFee = Long(w, "wallet_fee") != 0,
                FeePercent = Dec(w, "wallet_fee_percent") ?? 0.05m,
                FeeMinimum = Str(w, "wallet_fee_minimum") is null ? 1 : Long(w, "wallet_fee_minimum"),
                FeeBase = Long(w, "wallet_fee_base"),
                PublisherFeePercent = Dec(w, "wallet_publisher_fee_percent_default") ?? 0.10m,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? ParseSessionId(string html) => SessionId().Match(html) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The /market/ home page: wallet, session, the first page of active listings and every listing awaiting confirmation.</summary>
    public static MarketHome ParseHome(string html)
    {
        var wallet = ParseWallet(html);
        ulong.TryParse(PageSteamId().Match(html) is { Success: true } m ? m.Groups[1].Value : "", out var sid);
        var listings = ParseListingHtml(html, null, wallet);
        return new MarketHome(wallet, ParseSessionId(html), sid, listings, listings.Count(l => !l.AwaitingConfirmation));
    }

    /// <summary>/market/mylistings/render/ JSON.</summary>
    public static ListingsPage ParseListingsPage(string json, WalletInfo? wallet)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Bool(root, "success")) return new ListingsPage(false, 0, []);
            var html = Str(root, "results_html") ?? "";
            var hovers = Str(root, "hovers") ?? "";
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("assets", out var apps) && apps.ValueKind == JsonValueKind.Object)
                foreach (var app in apps.EnumerateObject())
                    if (app.Value.ValueKind == JsonValueKind.Object)
                        foreach (var ctx in app.Value.EnumerateObject())
                            if (ctx.Value.ValueKind == JsonValueKind.Object)
                                foreach (var a in ctx.Value.EnumerateObject())
                                    if (Str(a.Value, "market_hash_name") is { } n) names[a.Name] = n;
            var listings = ParseListingHtml(html, hovers, wallet)
                .Select(l => l.MarketHashName.Length == 0 && names.TryGetValue(l.AssetId, out var n) ? l with { MarketHashName = n } : l)
                .ToList();
            return new ListingsPage(true, (int)Long(root, "total_count"), listings);
        }
        catch (JsonException)
        {
            return new ListingsPage(false, 0, []);
        }
    }

    internal static List<MarketListing> ParseListingHtml(string html, string? hovers, WalletInfo? wallet)
    {
        var assets = new Dictionary<string, (string Ctx, string Asset)>(StringComparer.Ordinal);
        foreach (Match h in Hover().Matches(hovers ?? html))
            if (h.Groups[2].Value == "730") assets.TryAdd(h.Groups[1].Value, (h.Groups[3].Value, h.Groups[4].Value));

        var sections = SectionHeader().Matches(html).Select(s => (s.Index, Pending: s.Groups[1].Value.Contains("awaiting confirmation", StringComparison.OrdinalIgnoreCase))).ToList();
        var rows = ListingRow().Matches(html);
        var result = new List<MarketListing>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var id = rows[i].Groups[1].Value;
            var start = rows[i].Index;
            var end = i + 1 < rows.Count ? rows[i + 1].Index : html.Length;
            var nextSection = sections.FirstOrDefault(s => s.Index > start).Index;
            if (nextSection > start && nextSection < end) end = nextSection;
            var chunk = html[start..end];

            string ctx = "2", asset = "";
            if (ListingAction().Match(chunk) is { Success: true } act && act.Groups[1].Value == id)
            {
                if (act.Groups[2].Value != "730") continue;
                ctx = act.Groups[3].Value;
                asset = act.Groups[4].Value;
            }
            else if (assets.TryGetValue(id, out var a))
            {
                (ctx, asset) = a;
            }
            if (asset.Length == 0 || !seen.Add(id)) continue;

            var buyer = Cents(BuyerPrice().Match(chunk));
            var receive = Cents(ReceivePrice().Match(chunk));
            if (wallet is not null)
            {
                if (receive is null && buyer is { } b) receive = MarketFees.ForBuyerPrice(b, wallet).Receive;
                if (buyer is null && receive is { } r) buyer = MarketFees.ForReceive(r, wallet).BuyerPays;
            }
            var hash = ListingLink().Match(chunk) is { Success: true } link ? Uri.UnescapeDataString(link.Groups[1].Value) : "";
            var name = ItemName().Match(chunk) is { Success: true } nm ? WebUtility.HtmlDecode(nm.Groups[1].Value) : hash;
            var pending = sections.LastOrDefault(s => s.Index < start) is { Index: > 0 } sec && sec.Pending;
            result.Add(new MarketListing
            {
                ListingId = id,
                AssetId = asset,
                ContextId = ctx,
                Name = name,
                MarketHashName = hash,
                BuyerPays = buyer,
                Receive = receive,
                ListedOn = ListedDate().Match(chunk) is { Success: true } d ? WebUtility.HtmlDecode(d.Groups[1].Value) : "",
                AwaitingConfirmation = pending,
            });
        }
        return result;
    }

    private static long? Cents(Match m) =>
        m.Success && SteamPriceParser.Parse(WebUtility.HtmlDecode(m.Groups[1].Value)) is { } v ? MarketFees.ToCents(v) : null;

    /// <summary>/market/orderbook. The payload may be wrapped in one or two "data" envelopes.</summary>
    public static OrderBook? ParseOrderBook(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            for (var depth = 0; depth < 4 && e.ValueKind == JsonValueKind.Object; depth++)
            {
                if (e.TryGetProperty("amtMinSellOrder", out _) || e.TryGetProperty("rgCompactSellOrders", out _) || e.TryGetProperty("cSellOrders", out _))
                {
                    var min = Long(e, "amtMinSellOrder");
                    var max = Long(e, "amtMaxBuyOrder");
                    return new OrderBook(
                        (int)Long(e, "eCurrency"),
                        min > 0 ? min : null,
                        max > 0 ? max : null,
                        Long(e, "cSellOrders"),
                        Long(e, "cBuyOrders"),
                        Levels(e, "rgCompactSellOrders"),
                        Levels(e, "rgCompactBuyOrders"));
                }
                if (e.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return null;
                if (!e.TryGetProperty("data", out var inner)) return null;
                e = inner;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<OrderLevel> Levels(JsonElement e, string name)
    {
        var list = new List<OrderLevel>();
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        var values = arr.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0L).ToList();
        for (var i = 0; i + 1 < values.Count; i += 2)
            if (values[i] > 0) list.Add(new OrderLevel(values[i], values[i + 1]));
        return list;
    }

    /// <summary>/market/pricehistory/: median sale price per hour or day, in the wallet currency.</summary>
    public static List<PricePoint>? ParsePriceHistory(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Bool(root, "success")) return null;
            var list = new List<PricePoint>();
            if (!root.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array) return list;
            foreach (var p in prices.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 3) continue;
                var m = HistoryDate().Match(p[0].GetString() ?? "");
                if (!m.Success) continue;
                if (!DateTime.TryParseExact($"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value} {m.Groups[4].Value}", "MMM d yyyy H",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) continue;
                if (p[1].ValueKind != JsonValueKind.Number || !p[1].TryGetDecimal(out var price)) continue;
                var volume = p[2].ValueKind == JsonValueKind.String && int.TryParse(p[2].GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v
                    : p[2].ValueKind == JsonValueKind.Number && p[2].TryGetInt32(out var vn) ? vn : 0;
                list.Add(new PricePoint(new DateTimeOffset(at, TimeSpan.Zero), price, volume));
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Volume-weighted median of the points since <paramref name="since"/>.</summary>
    public static decimal? Median(IEnumerable<PricePoint> points, DateTimeOffset since)
    {
        var recent = points.Where(p => p.At >= since && p.Price > 0).OrderBy(p => p.Price).ToList();
        if (recent.Count == 0) return null;
        long total = recent.Sum(p => (long)Math.Max(1, p.Volume));
        long run = 0;
        foreach (var p in recent)
        {
            run += Math.Max(1, p.Volume);
            if (run * 2 >= total) return p.Price;
        }
        return recent[^1].Price;
    }

    public static SellResult ParseSellResponse(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var message = Str(root, "message") is { Length: > 0 } msg ? WebUtility.HtmlDecode(msg) : null;
                if (Bool(root, "success"))
                {
                    var mobile = Bool(root, "needs_mobile_confirmation");
                    var email = Bool(root, "needs_email_confirmation");
                    var needs = Bool(root, "requires_confirmation") || mobile || email;
                    var domain = Str(root, "email_domain");
                    var text = mobile ? "Listed. Confirm it in the Steam Mobile app."
                        : email ? "Listed. Confirm it in the email Steam sent" + (string.IsNullOrEmpty(domain) ? "." : $" to your {domain} address.")
                        : needs ? "Listed. Steam needs you to confirm it."
                        : "Listed on the Steam Market.";
                    return new SellResult(true, mobile || (needs && !email), email, domain, text);
                }
                return new SellResult(false, false, false, null, message ?? (status == 200 ? "Steam refused the listing." : $"Steam answered {status}."));
            }
        }
        catch (JsonException)
        {
        }
        return new SellResult(false, false, false, null, status switch
        {
            < 0 => "Couldn't reach Steam.",
            400 => "Steam refused the request. Sign in again and retry.",
            401 or 403 => "Steam sign-in expired. Sign in again.",
            429 => "Steam is rate-limiting requests. Wait a minute and retry.",
            _ => $"Steam answered {status}.",
        });
    }

    /// <summary>Returns the JSON object that starts at <paramref name="open"/>, honouring strings and escapes.</summary>
    internal static string? ExtractObject(string s, int open)
    {
        if (open < 0 || open >= s.Length || s[open] != '{') return null;
        var depth = 0;
        var inString = false;
        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return s[open..(i + 1)];
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) => Steam.InventoryParser.Str(e, name);

    private static long Long(JsonElement e, string name) =>
        long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static decimal? Dec(JsonElement e, string name) =>
        decimal.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool Bool(JsonElement e, string name) => Str(e, name) is "1" or "true";
}
