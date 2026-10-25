using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Cs2DropChecker.Core.Settings;
using Cs2DropChecker.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cs2DropChecker.Core.Pricing;

public sealed record PriceQuote(decimal Price, string Currency, string SourceId, DateTimeOffset AsOf);

public interface IPriceSource
{
    string Id { get; }
    string DisplayName { get; }
    DateTimeOffset? LastUpdated { get; }
    PriceQuote? TryGetCached(string marketHashName, string currency);
}

public interface IBulkPriceSource : IPriceSource
{
    Task RefreshIfStaleAsync(string currency, CancellationToken ct);
}

public interface ILivePriceSource : IPriceSource
{
    Task<PriceQuote?> FetchAsync(string marketHashName, string currency, CancellationToken ct);
}

public static class PriceSourceIds
{
    public const string SteamLive = "steam-live";
    public const string SteamFeed = "steam-feed";
    public const string Skinport = "skinport";
    public static readonly string[] All = [SteamLive, SteamFeed, Skinport];

    public static string CardName(string? id) => id switch
    {
        SteamLive => "Steam",
        SteamFeed => "Steam 24h",
        Skinport => "Skinport",
        _ => "no price",
    };

    public static string TableName(string? id) => id switch
    {
        SteamLive => "Steam Market",
        SteamFeed => "Steam 24h",
        Skinport => "Skinport",
        _ => "—",
    };
}

public sealed record CurrencyInfo(string Code, string Symbol, int SteamCode, bool SymbolAfter);

public static class Currencies
{
    public static readonly IReadOnlyList<CurrencyInfo> All =
    [
        new("EUR", "€", 3, false),
        new("USD", "$", 1, false),
        new("GBP", "£", 2, false),
        new("CHF", "CHF ", 4, false),
        new("PLN", " zł", 6, true),
        new("BRL", "R$ ", 7, false),
        new("NOK", " kr", 9, true),
        new("CAD", "CA$ ", 20, false),
        new("AUD", "A$ ", 21, false),
    ];

    /// <summary>Other Steam wallet currencies. The market always trades in the wallet currency, which may not be in <see cref="All"/>.</summary>
    private static readonly IReadOnlyList<CurrencyInfo> WalletOnly =
    [
        new("RUB", " ₽", 5, true),
        new("JPY", "¥ ", 8, false),
        new("IDR", "Rp ", 10, false),
        new("MYR", "RM", 11, false),
        new("PHP", "P", 12, false),
        new("SGD", "S$", 13, false),
        new("THB", "฿", 14, false),
        new("VND", "₫", 15, true),
        new("KRW", "₩ ", 16, false),
        new("TRY", " TL", 17, true),
        new("UAH", "₴", 18, true),
        new("MXN", "Mex$ ", 19, false),
        new("NZD", "NZ$ ", 22, false),
        new("CNY", "¥ ", 23, false),
        new("INR", "₹ ", 24, false),
        new("CLP", "CLP$ ", 25, false),
        new("PEN", "S/.", 26, false),
        new("COP", "COL$ ", 27, false),
        new("ZAR", "R ", 28, false),
        new("HKD", "HK$ ", 29, false),
        new("TWD", "NT$ ", 30, false),
        new("SAR", " SR", 31, true),
        new("AED", " AED", 32, true),
        new("ARS", "ARS$ ", 34, false),
        new("ILS", "₪", 35, false),
        new("KZT", "₸", 37, true),
        new("KWD", " KD", 38, true),
        new("QAR", " QR", 39, true),
        new("CRC", "₡", 40, false),
        new("UYU", "$U", 41, false),
    ];

    public static CurrencyInfo Get(string? code) => All.FirstOrDefault(c => c.Code == code) ?? All[0];

    public static bool IsKnown(string? code) => All.Any(c => c.Code == code);

    public static CurrencyInfo FromSteamCode(int steamCode) =>
        All.FirstOrDefault(c => c.SteamCode == steamCode)
        ?? WalletOnly.FirstOrDefault(c => c.SteamCode == steamCode)
        ?? new CurrencyInfo($"#{steamCode}", "", steamCode, true);

    public static string Format(decimal value, string? code)
    {
        if (value < 0) return "-" + Format(-value, code);
        var c = code is null ? All[0] : All.FirstOrDefault(x => x.Code == code) ?? WalletOnly.FirstOrDefault(x => x.Code == code) ?? new CurrencyInfo(code, " " + code, 0, true);
        var n = value.ToString("#,##0.00", CultureInfo.InvariantCulture);
        return c.SymbolAfter ? n + c.Symbol : c.Symbol + n;
    }
}

public static class SteamPriceParser
{
    /// <summary>Parses Steam's localised price strings such as "28,86€", "$1,234.56", "12,--€" or "1 234,56 zł".</summary>
    public static decimal? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Replace("--", "00", StringComparison.Ordinal);
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t)
            if (char.IsAsciiDigit(ch) || ch == ',' || ch == '.') sb.Append(ch);
        var n = sb.ToString().Trim('.', ',');
        if (n.Length == 0) return null;

        var last = Math.Max(n.LastIndexOf(','), n.LastIndexOf('.'));
        string normalized;
        if (last >= 0 && n.Length - last - 1 is 1 or 2)
            normalized = n[..last].Replace(",", "").Replace(".", "") + "." + n[(last + 1)..];
        else
            normalized = n.Replace(",", "").Replace(".", "");
        if (normalized.StartsWith('.')) normalized = "0" + normalized;
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

/// <summary>Steam Community Market priceoverview: the 24 h median in the user's currency, one item per request.</summary>
public sealed class SteamLivePriceSource(HttpClient http, AppDatabase? db = null, ILogger? log = null) : ILivePriceSource
{
    private readonly ConcurrentDictionary<(string, string), PriceQuote> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    public TimeSpan Spacing { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(12);
    public string Id => PriceSourceIds.SteamLive;
    public string DisplayName => "Steam 24 h median, live";
    public DateTimeOffset? LastUpdated { get; private set; }

    public PriceQuote? TryGetCached(string marketHashName, string currency) =>
        _cache.TryGetValue((marketHashName, currency), out var q) && DateTimeOffset.UtcNow - q.AsOf < MaxAge ? q : null;

    /// <summary>Loads earlier live prices from the database so lookups never hit SQLite per item.</summary>
    public void Warm(string currency)
    {
        if (db is null) return;
        foreach (var (name, quote) in db.GetCachedPrices(Id, currency))
            _cache[(name, currency)] = quote;
    }

    public async Task<PriceQuote?> FetchAsync(string marketHashName, string currency, CancellationToken ct)
    {
        if (TryGetCached(marketHashName, currency) is { } cached) return cached;
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _next - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            var code = Currencies.Get(currency).SteamCode;
            var url = $"https://steamcommunity.com/market/priceoverview/?appid=730&currency={code}&market_hash_name={Uri.EscapeDataString(marketHashName)}";
            using var resp = await http.GetAsync(url, ct);
            _next = DateTimeOffset.UtcNow + Spacing;
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _next = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
                _log.LogInformation("Steam priceoverview rate-limited; pausing live prices for 60 s");
                return null;
            }
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Get(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var price = SteamPriceParser.Parse(Get("median_price")) ?? SteamPriceParser.Parse(Get("lowest_price"));
            if (price is null) return null;
            var quote = new PriceQuote(price.Value, currency, Id, DateTimeOffset.UtcNow);
            _cache[(marketHashName, currency)] = quote;
            db?.UpsertPrice(marketHashName, Id, currency, price.Value, quote.AsOf);
            LastUpdated = quote.AsOf;
            return quote;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _log.LogInformation("Live price for {Item} failed: {Message}", marketHashName, ex.Message);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Daily ECB reference rates (frankfurter.dev), USD based.</summary>
public sealed class FxService(HttpClient http, string cacheDir)
{
    private readonly string _file = Path.Combine(cacheDir, "fx_usd.json");
    private volatile Dictionary<string, decimal> _rates = new() { ["USD"] = 1m };
    private bool _loadedDisk;

    public DateTimeOffset? LastUpdated { get; private set; }

    public async Task RefreshIfStaleAsync(CancellationToken ct)
    {
        if (!_loadedDisk)
        {
            _loadedDisk = true;
            LoadDisk();
        }
        if (LastUpdated is { } t && DateTimeOffset.UtcNow - t < TimeSpan.FromHours(12) && _rates.Count > 1) return;
        var codes = string.Join(',', Currencies.All.Select(c => c.Code).Where(c => c != "USD"));
        var json = await http.GetStringAsync($"https://api.frankfurter.dev/v1/latest?base=USD&symbols={codes}", ct);
        var parsed = Parse(json);
        if (parsed.Count <= 1) return;
        _rates = parsed;
        LastUpdated = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(_file, json, ct);
    }

    internal void SetRates(Dictionary<string, decimal> rates)
    {
        rates["USD"] = 1m;
        _rates = rates;
        LastUpdated = DateTimeOffset.UtcNow;
        _loadedDisk = true;
    }

    internal static Dictionary<string, decimal> Parse(string json)
    {
        var rates = new Dictionary<string, decimal> { ["USD"] = 1m };
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("rates", out var r) && r.ValueKind == JsonValueKind.Object)
            foreach (var p in r.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDecimal(out var v) && v > 0)
                    rates[p.Name] = v;
        return rates;
    }

    public decimal? Rate(string from, string to)
    {
        if (from == to) return 1m;
        var r = _rates;
        if (!r.TryGetValue(from, out var f) || !r.TryGetValue(to, out var t) || f == 0) return null;
        return t / f;
    }

    private void LoadDisk()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var parsed = Parse(File.ReadAllText(_file));
            if (parsed.Count <= 1) return;
            _rates = parsed;
            LastUpdated = new DateTimeOffset(File.GetLastWriteTimeUtc(_file), TimeSpan.Zero);
        }
        catch (Exception) { }
    }
}

/// <summary>CSGO Trader's daily Steam price export: 24 h median for every item, in USD.</summary>
public sealed class CsgoTraderSteamSource(HttpClient http, FxService fx, string cacheDir) : IBulkPriceSource
{
    private static readonly string[] Keys = ["last_24h", "last_7d", "last_30d", "last_90d"];
    private readonly string _file = Path.Combine(cacheDir, "csgotrader_steam.json");
    private volatile Dictionary<string, decimal> _usd = new(StringComparer.Ordinal);
    private bool _loadedDisk;

    public TimeSpan RefreshEvery { get; init; } = TimeSpan.FromHours(6);
    public string Id => PriceSourceIds.SteamFeed;
    public string DisplayName => "Steam 24 h median, full list";
    public DateTimeOffset? LastUpdated { get; private set; }
    public int Count => _usd.Count;

    public async Task RefreshIfStaleAsync(string currency, CancellationToken ct)
    {
        if (!_loadedDisk)
        {
            _loadedDisk = true;
            await Task.Run(LoadDisk, ct);
        }
        if (LastUpdated is { } t && DateTimeOffset.UtcNow - t < RefreshEvery && _usd.Count > 0) return;
        var json = await http.GetStringAsync("https://prices.csgotrader.app/latest/steam.json", ct);
        var parsed = await Task.Run(() => Parse(json), ct);
        if (parsed.Count < 1000) throw new InvalidDataException("The CSGO Trader feed looked incomplete.");
        _usd = parsed;
        LastUpdated = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(_file, json, ct);
    }

    internal void SetPrices(Dictionary<string, decimal> usd)
    {
        _usd = usd;
        LastUpdated = DateTimeOffset.UtcNow;
        _loadedDisk = true;
    }

    internal static Dictionary<string, decimal> Parse(string json)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var key in Keys)
                if (p.Value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d > 0)
                {
                    result[p.Name] = d;
                    break;
                }
        }
        return result;
    }

    public PriceQuote? TryGetCached(string marketHashName, string currency)
    {
        if (!_usd.TryGetValue(marketHashName, out var usd)) return null;
        if (fx.Rate("USD", currency) is not { } rate) return null;
        return new PriceQuote(Math.Round(usd * rate, 2), currency, Id, LastUpdated ?? DateTimeOffset.UtcNow);
    }

    private void LoadDisk()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var parsed = Parse(File.ReadAllText(_file));
            if (parsed.Count < 1000) return;
            _usd = parsed;
            LastUpdated = new DateTimeOffset(File.GetLastWriteTimeUtc(_file), TimeSpan.Zero);
        }
        catch (Exception) { }
    }
}

/// <summary>Skinport's public item list. Needs Brotli and allows 8 requests per 5 minutes.</summary>
public sealed class SkinportSource(HttpClient brotliHttp, string cacheDir) : IBulkPriceSource
{
    private volatile Dictionary<string, decimal> _prices = new(StringComparer.Ordinal);
    private string _currency = "";
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    public TimeSpan RefreshEvery { get; init; } = TimeSpan.FromMinutes(10);
    public string Id => PriceSourceIds.Skinport;
    public string DisplayName => "Skinport";
    public DateTimeOffset? LastUpdated { get; private set; }

    private string FileFor(string currency) => Path.Combine(cacheDir, $"skinport_{currency}.json");

    public async Task RefreshIfStaleAsync(string currency, CancellationToken ct)
    {
        if (_currency != currency)
        {
            _currency = currency;
            _prices = new(StringComparer.Ordinal);
            LastUpdated = null;
            await Task.Run(() => LoadDisk(currency), ct);
        }
        if (LastUpdated is { } t && DateTimeOffset.UtcNow - t < RefreshEvery && _prices.Count > 0) return;
        if (DateTimeOffset.UtcNow < _retryAfter) return;
        using var resp = await brotliHttp.GetAsync($"https://api.skinport.com/v1/items?app_id=730&currency={currency}&tradable=0", ct);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _retryAfter = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
            return;
        }
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var parsed = await Task.Run(() => Parse(json), ct);
        if (parsed.Count < 1000) throw new InvalidDataException("The Skinport list looked incomplete.");
        _prices = parsed;
        LastUpdated = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(FileFor(currency), json, ct);
    }

    internal void SetPrices(string currency, Dictionary<string, decimal> prices)
    {
        _currency = currency;
        _prices = prices;
        LastUpdated = DateTimeOffset.UtcNow;
    }

    internal static Dictionary<string, decimal> Parse(string json)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("market_hash_name", out var n) || n.ValueKind != JsonValueKind.String) continue;
            foreach (var key in (string[])["suggested_price", "min_price", "median_price"])
                if (e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d > 0)
                {
                    result[n.GetString()!] = d;
                    break;
                }
        }
        return result;
    }

    public PriceQuote? TryGetCached(string marketHashName, string currency) =>
        currency == _currency && _prices.TryGetValue(marketHashName, out var p)
            ? new PriceQuote(p, currency, Id, LastUpdated ?? DateTimeOffset.UtcNow)
            : null;

    private void LoadDisk(string currency)
    {
        try
        {
            var file = FileFor(currency);
            if (!File.Exists(file)) return;
            var parsed = Parse(File.ReadAllText(file));
            if (parsed.Count < 1000) return;
            _prices = parsed;
            LastUpdated = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        }
        catch (Exception) { }
    }
}

/// <summary>Applies the user's source order: the first enabled source with a price wins.</summary>
public sealed class PriceService(
    SteamLivePriceSource live,
    CsgoTraderSteamSource feed,
    SkinportSource skinport,
    FxService fx,
    Func<AppSettings> settings,
    ILogger? log = null)
{
    private readonly ILogger _log = log ?? NullLogger.Instance;

    public SteamLivePriceSource Live => live;
    public CsgoTraderSteamSource Feed => feed;
    public SkinportSource Skinport => skinport;
    public FxService Fx => fx;
    public IReadOnlyList<IPriceSource> All => [live, feed, skinport];
    public string Currency => settings().Currency;

    public event EventHandler? PricesUpdated;

    public IEnumerable<IPriceSource> Ordered()
    {
        foreach (var s in settings().PriceSources.ToArray())
            if (s.Enabled && All.FirstOrDefault(x => x.Id == s.Id) is { } src)
                yield return src;
    }

    public PriceQuote? GetCached(string marketHashName)
    {
        var currency = Currency;
        foreach (var s in Ordered())
            if (s.TryGetCached(marketHashName, currency) is { } q)
                return q;
        return null;
    }

    public async Task<PriceQuote?> GetForDropAsync(string marketHashName, CancellationToken ct)
    {
        var currency = Currency;
        foreach (var s in Ordered())
        {
            var q = s is ILivePriceSource l ? await l.FetchAsync(marketHashName, currency, ct) : s.TryGetCached(marketHashName, currency);
            if (q is not null) return q;
        }
        return null;
    }

    public async Task RefreshBulkAsync(CancellationToken ct)
    {
        var currency = Currency;
        var changed = false;
        var fxBefore = fx.LastUpdated;
        try { await fx.RefreshIfStaleAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning("FX refresh failed: {Message}", ex.Message); }
        if (fx.LastUpdated != fxBefore) changed = true;

        foreach (var s in Ordered().OfType<IBulkPriceSource>().ToList())
        {
            var before = s.LastUpdated;
            try { await s.RefreshIfStaleAsync(currency, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning("{Source} refresh failed: {Message}", s.Id, ex.Message); }
            if (s.LastUpdated != before) changed = true;
        }
        if (changed) PricesUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseUpdated() => PricesUpdated?.Invoke(this, EventArgs.Empty);
}
