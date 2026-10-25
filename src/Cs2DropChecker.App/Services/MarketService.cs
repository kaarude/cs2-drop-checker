using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Cs2DropChecker.Core.Market;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Steam;
using Cs2DropChecker.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Services;

public sealed record SoldEventArgs(IReadOnlyList<SaleRecord> Sales);

/// <summary>
/// Steam Community Market actions for the signed-in account: wallet, listings, selling and cancelling. Every request
/// runs inside the signed-in browser, except order books, which Steam serves to anyone. Events fire on the UI thread.
/// </summary>
public sealed class MarketService
{
    private static readonly TimeSpan BookAge = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HistoryAge = TimeSpan.FromMinutes(15);

    private readonly AppServices _s;
    private readonly ILogger _log;
    private readonly MarketLedger _ledger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _bookGate = new(1, 1);
    private readonly ConcurrentDictionary<(string, int), (DateTimeOffset At, OrderBook Book)> _books = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, List<PricePoint> Points)> _history = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _justListed = new(StringComparer.Ordinal);
    private DateTimeOffset _nextBook = DateTimeOffset.MinValue;
    private DateTimeOffset _walletAt = DateTimeOffset.MinValue;
    private Dictionary<string, DateTimeOffset> _firstSeen = new(StringComparer.Ordinal);
    private ulong _firstSeenFor;
    private volatile IReadOnlyList<ListingRow> _listings = [];

    public MarketService(AppServices s, ILogger log)
    {
        _s = s;
        _log = log;
        _ledger = new MarketLedger(s.Db);
        s.Session.Changed += (_, _) => Raise();
    }

    public WalletInfo? Wallet { get; private set; }
    public string? SessionId { get; private set; }
    public DateTimeOffset? ListingsAt { get; private set; }
    public string LastError { get; private set; } = "";
    public bool Busy { get; private set; }

    /// <summary>Listings that are live or awaiting confirmation.</summary>
    public IReadOnlyList<ListingRow> Listings => _listings;

    public event EventHandler? Changed;
    public event EventHandler<SoldEventArgs>? Sold;

    /// <summary>Selling needs the built-in browser signed in to the watched account.</summary>
    public bool CanSell
    {
        get
        {
            var s = _s.Settings;
            return s.UseSignIn && _s.Session.IsSignedIn && s.SteamId64 != 0 && _s.Session.SignedInSteamId == s.SteamId64;
        }
    }

    public string CurrencyIso => Wallet?.CurrencyIso ?? _s.Settings.Currency;

    private void Raise() => _s.Ui.BeginInvoke(() => Changed?.Invoke(this, EventArgs.Empty));

    public void LoadSaved()
    {
        var id = _s.Settings.SteamId64;
        if (id == 0) return;
        try
        {
            _listings = _s.Db.GetListings(id).Where(l => l.State != ListingState.Gone).ToList();
            LoadFirstSeen(id);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not load saved listings: {Message}", ex.Message);
        }
        Raise();
    }

    public void Reset()
    {
        Wallet = null;
        SessionId = null;
        ListingsAt = null;
        _walletAt = DateTimeOffset.MinValue;
        _justListed.Clear();
        _listings = [];
        _firstSeen = new(StringComparer.Ordinal);
        _firstSeenFor = 0;
        LoadSaved();
    }

    public bool IsListed(string assetId) =>
        _justListed.ContainsKey(assetId) || _listings.Any(l => l.AssetId == assetId);

    public ListingRow? ListingFor(string assetId) => _listings.FirstOrDefault(l => l.AssetId == assetId);

    public bool JustListed(string assetId) => _justListed.ContainsKey(assetId);

    public Sellability Evaluate(InventoryItem item)
    {
        var id = _s.Settings.SteamId64;
        if (_firstSeenFor != id) LoadFirstSeen(id);
        DateTimeOffset? seen = _firstSeen.TryGetValue(item.AssetId, out var f) ? f : null;
        return SellRules.Evaluate(item, IsListed(item.AssetId), seen, DateTimeOffset.UtcNow);
    }

    /// <summary>Assets whose disappearance is a sale rather than a consumed or moved item.</summary>
    public HashSet<string> MarketAssets()
    {
        var set = new HashSet<string>(_justListed.Keys, StringComparer.Ordinal);
        try { set.UnionWith(_ledger.ListedAssets(_s.Settings.SteamId64)); }
        catch (Exception) { }
        return set;
    }

    private void LoadFirstSeen(ulong id)
    {
        try { _firstSeen = id == 0 ? new(StringComparer.Ordinal) : _s.Db.GetFirstSeen(id); }
        catch (Exception) { _firstSeen = new(StringComparer.Ordinal); }
        _firstSeenFor = id;
    }

    /// <summary>Called by the watcher after every saved snapshot, on its thread.</summary>
    public async Task OnSnapshotAsync(InventorySnapshot snap, CancellationToken ct)
    {
        if (!snap.SignedIn || snap.SteamId != _s.Settings.SteamId64) return;
        try
        {
            var waiting = snap.Items.Where(i => i.ContextId == InventoryFetcher.ProtectedContext || !i.Marketable).Select(i => i.AssetId).ToList();
            if (waiting.Count > 0) _s.Db.TouchFirstSeen(snap.SteamId, waiting, snap.TakenAt);
            LoadFirstSeen(snap.SteamId);

            var sales = _ledger.Resolve(snap.SteamId, snap);
            if (sales.Count > 0)
            {
                _log.LogInformation("{Count} listing(s) sold", sales.Count);
                _ = _s.Ui.BeginInvoke(() => Sold?.Invoke(this, new SoldEventArgs(sales)));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Market bookkeeping failed: {Message}", ex.Message);
        }

        var due = ListingsAt is null || DateTimeOffset.UtcNow - ListingsAt.Value > TimeSpan.FromMinutes(20)
            || !_justListed.IsEmpty || _listings.Count > 0 || HasGoneListings(snap.SteamId);
        if (CanSell && due) await RefreshAsync(ct);
    }

    private bool HasGoneListings(ulong id)
    {
        try { return _s.Db.GetListings(id).Any(l => l.State == ListingState.Gone); }
        catch (Exception) { return false; }
    }

    /// <summary>Reloads the wallet (at most every few minutes unless forced) and every listing.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct, bool forceWallet = false)
    {
        if (!CanSell)
        {
            LastError = "Sign in to Steam under Settings to sell.";
            Raise();
            return false;
        }
        await _gate.WaitAsync(ct);
        Busy = true;
        Raise();
        try
        {
            var id = _s.Settings.SteamId64;
            if (forceWallet || Wallet is null || DateTimeOffset.UtcNow - _walletAt > TimeSpan.FromMinutes(5))
            {
                var home = await _s.Session.FetchAsync("/market/?l=english", ct);
                if (!Check(home, out var err))
                {
                    LastError = err;
                    return false;
                }
                var parsed = MarketParsers.ParseHome(home.Body);
                if (parsed.SteamId != 0 && parsed.SteamId != id)
                {
                    LastError = "The built-in browser is signed in to a different Steam account.";
                    return false;
                }
                if (parsed.Wallet is not null)
                {
                    Wallet = parsed.Wallet;
                    _walletAt = DateTimeOffset.UtcNow;
                }
                SessionId = parsed.SessionId ?? SessionId;
            }

            var all = new Dictionary<string, MarketListing>(StringComparer.Ordinal);
            var first = await _s.Session.FetchAsync("/market/mylistings?count=100&l=english", ct);
            if (!Check(first, out var e1))
            {
                LastError = e1;
                return false;
            }
            var page = MarketParsers.ParseListingsPage(first.Body, Wallet);
            if (!page.Success)
            {
                LastError = "Steam did not return your listings.";
                return false;
            }
            foreach (var l in page.Listings) all.TryAdd(l.ListingId, l);
            for (var start = 100; start < page.TotalCount && start < 2000; start += 100)
            {
                await Task.Delay(800, ct);
                var r = await _s.Session.FetchAsync($"/market/mylistings/render/?query=&start={start}&count=100&l=english", ct);
                var more = r.Status == 200 ? MarketParsers.ParseListingsPage(r.Body, Wallet) : null;
                if (more is not { Success: true })
                {
                    LastError = "Steam did not return every listing page.";
                    return false;
                }
                foreach (var l in more.Listings) all.TryAdd(l.ListingId, l);
                if (more.Listings.Count == 0) break;
            }

            var now = DateTimeOffset.UtcNow;
            _ledger.ApplyListings(id, all.Values.ToList(), CurrencyIso, _s.Watcher.Current, now);
            _listings = _s.Db.GetListings(id).Where(l => l.State != ListingState.Gone).ToList();
            foreach (var (asset, at) in _justListed)
                if (_listings.Any(l => l.AssetId == asset) || now - at > TimeSpan.FromMinutes(3))
                    _justListed.TryRemove(asset, out _);
            ListingsAt = now;
            LastError = "";
            _log.LogInformation("Listings: {Active} active, {Pending} awaiting confirmation", _listings.Count(l => l.State == ListingState.Active), _listings.Count(l => l.State == ListingState.Pending));
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = "Market refresh failed: " + ex.Message;
            _log.LogWarning("Market refresh failed: {Message}", ex.Message);
            return false;
        }
        finally
        {
            Busy = false;
            _gate.Release();
            Raise();
        }
    }

    private bool Check(BridgeResponse r, out string error)
    {
        error = "";
        if (r.Url.Contains("/login", StringComparison.OrdinalIgnoreCase) || r.Status is 401 or 403)
        {
            error = "Steam sign-in expired. Sign in again under Settings.";
            _s.Session.MarkSignedOut();
            return false;
        }
        if (r.Status == 429)
        {
            error = "Steam is rate-limiting requests. Try again in a minute.";
            return false;
        }
        if (r.Status != 200)
        {
            error = r.Status < 0 ? "Couldn't reach Steam." : $"Steam answered {r.Status}.";
            return false;
        }
        return true;
    }

    public Task<WalletInfo?> EnsureWalletAsync(CancellationToken ct) => Wallet is not null && DateTimeOffset.UtcNow - _walletAt < TimeSpan.FromMinutes(5)
        ? Task.FromResult<WalletInfo?>(Wallet)
        : RefreshAsync(ct, forceWallet: true).ContinueWith(_ => Wallet, ct, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Lowest listing and highest buy order. Steam serves this without sign-in; requests are spaced out.</summary>
    public async Task<OrderBook?> GetOrderBookAsync(string marketHashName, int currencyCode, CancellationToken ct)
    {
        if (_books.TryGetValue((marketHashName, currencyCode), out var c) && DateTimeOffset.UtcNow - c.At < BookAge) return c.Book;
        await _bookGate.WaitAsync(ct);
        try
        {
            var wait = _nextBook - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            var country = Wallet?.Country is { Length: 2 } cc ? cc : "US";
            var qp = Uri.EscapeDataString(JsonSerializer.Serialize(new object[] { 730, marketHashName }));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://steamcommunity.com/market/orderbook?q=Load&qp={qp}&cc={country}&l=english&currency={currencyCode}");
            req.Headers.TryAddWithoutValidation("x-valve-request-type", "queryAction");
            req.Headers.Referrer = new Uri("https://steamcommunity.com/market/listings/730/" + Uri.EscapeDataString(marketHashName));
            using var resp = await _s.Http.SendAsync(req, ct);
            _nextBook = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1.5);
            if ((int)resp.StatusCode == 429)
            {
                _nextBook = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
                return null;
            }
            if (!resp.IsSuccessStatusCode) return null;
            var book = MarketParsers.ParseOrderBook(await resp.Content.ReadAsStringAsync(ct));
            if (book is not null) _books[(marketHashName, currencyCode)] = (DateTimeOffset.UtcNow, book);
            return book;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _log.LogInformation("Order book for {Item} failed: {Message}", marketHashName, ex.Message);
            return null;
        }
        finally
        {
            _bookGate.Release();
        }
    }

    /// <summary>Median sale prices in the wallet currency. Steam only serves this to signed-in users.</summary>
    public async Task<List<PricePoint>?> GetPriceHistoryAsync(string marketHashName, CancellationToken ct)
    {
        if (_history.TryGetValue(marketHashName, out var c) && DateTimeOffset.UtcNow - c.At < HistoryAge) return c.Points;
        if (!CanSell) return null;
        var r = await _s.Session.FetchAsync($"/market/pricehistory/?appid=730&market_hash_name={Uri.EscapeDataString(marketHashName)}", ct);
        if (r.Status != 200) return null;
        var points = MarketParsers.ParsePriceHistory(r.Body);
        if (points is not null) _history[marketHashName] = (DateTimeOffset.UtcNow, points);
        return points;
    }

    /// <summary>Lists one item. <paramref name="receive"/> is what the seller gets, in hundredths of the wallet currency.</summary>
    public async Task<SellResult> SellAsync(InventoryItem item, long receive, CancellationToken ct)
    {
        if (!CanSell) return new SellResult(false, false, false, null, "Sign in to Steam under Settings to sell.");
        if (receive < 1) return new SellResult(false, false, false, null, "The price is too low to list.");
        await _gate.WaitAsync(ct);
        try
        {
            var id = _s.Settings.SteamId64;
            var form = new List<KeyValuePair<string, string>>
            {
                new("appid", "730"),
                new("contextid", item.ContextId),
                new("assetid", item.AssetId),
                new("amount", "1"),
                new("price", receive.ToString(CultureInfo.InvariantCulture)),
            };
            var r = await _s.Session.PostFormAsync("/market/sellitem/", form, $"https://steamcommunity.com/profiles/{id}/inventory/", SessionId, ct);
            var result = MarketParsers.ParseSellResponse(r.Status, r.Body);
            _log.LogInformation("Sell {Item} ({Asset}) for {Receive}: HTTP {Status}, {Outcome}", item.MarketHashName, item.AssetId, receive, r.Status, result.Success ? "listed" : result.Message);
            if (result.Success) _justListed[item.AssetId] = DateTimeOffset.UtcNow;
            else if (r.Status is 401 or 403) _s.Session.MarkSignedOut();
            return result;
        }
        finally
        {
            _gate.Release();
            Raise();
        }
    }

    public async Task<(bool Ok, string Message)> CancelAsync(ListingRow listing, CancellationToken ct)
    {
        if (!CanSell) return (false, "Sign in to Steam under Settings first.");
        await _gate.WaitAsync(ct);
        try
        {
            var r = await _s.Session.PostFormAsync($"/market/removelisting/{listing.ListingId}", [], "https://steamcommunity.com/market/", SessionId, ct);
            _log.LogInformation("Remove listing {Listing}: HTTP {Status}", listing.ListingId, r.Status);
            if (r.Status != 200)
                return (false, r.Status switch
                {
                    < 0 => "Couldn't reach Steam.",
                    401 or 403 => "Steam sign-in expired. Sign in again under Settings.",
                    429 => "Steam is rate-limiting requests. Try again in a minute.",
                    _ => $"Steam answered {r.Status}. The listing may already be sold or removed.",
                });
            _ledger.Forget(listing.ListingId);
            _justListed.TryRemove(listing.AssetId, out _);
            _listings = _listings.Where(l => l.ListingId != listing.ListingId).ToList();
            return (true, listing.State == ListingState.Pending ? "Listing cancelled." : "Listing removed. The item is back to normal in your inventory.");
        }
        finally
        {
            _gate.Release();
            Raise();
        }
    }

    public List<SaleRecord> Sales(int limit = 200)
    {
        try { return _s.Db.GetSales(_s.Settings.SteamId64, limit); }
        catch (Exception) { return []; }
    }

    /// <summary>The three suggested buyer prices for an item, in hundredths of the wallet currency.</summary>
    public static (long? Undercut, long? BuyOrder, long? Median) Suggestions(OrderBook? book, IReadOnlyList<PricePoint>? history, DateTimeOffset now)
    {
        long? undercut = book?.LowestSell is { } low ? Math.Max(3, low - 1) : null;
        long? buy = book?.HighestBuy is { } hb && hb >= 3 ? hb : null;
        long? median = history is null ? null : MarketParsers.Median(history, now - TimeSpan.FromHours(24)) is { } m ? Math.Max(3, MarketFees.ToCents(m)) : null;
        return (undercut, buy, median);
    }
}
