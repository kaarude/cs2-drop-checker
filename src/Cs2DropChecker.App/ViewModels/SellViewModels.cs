using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Market;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Storage;

namespace Cs2DropChecker.App.ViewModels;

public sealed partial class PriceChoice(string key, string label) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public long? Buyer { get; set; }

    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private string _priceText = "—";
    [ObservableProperty] private string _receiveText = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _available;
}

/// <summary>Shared helpers for the sell dialogs. Market amounts are hundredths of the wallet currency.</summary>
public static class SellText
{
    public static string Cents(long cents, string currency) => Money.Format(MarketFees.FromCents(cents), currency);

    /// <summary>Plain number for the custom price boxes: "12.34".</summary>
    public static string Plain(long cents) => MarketFees.FromCents(cents).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Reads "12,34", "12.34", "€ 1,234.50" or "12".</summary>
    public static long? ParseCents(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = new string(text.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray());
        if (s.Length == 0) return null;
        var last = s.LastIndexOfAny(['.', ',']);
        if (last >= 0)
        {
            var decimals = s.Length - last - 1;
            var whole = s[..last].Replace(".", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
            var frac = s[(last + 1)..];
            s = decimals is >= 1 and <= 2 ? whole + "." + frac : whole + frac;
        }
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < 1_000_000m ? MarketFees.ToCents(v) : null;
    }

    public static (decimal? Price, string Label) MedianWithFallback(IReadOnlyList<PricePoint>? history, DateTimeOffset now)
    {
        if (history is null) return (null, "24 h median");
        if (MarketParsers.Median(history, now - TimeSpan.FromHours(24)) is { } day) return (day, "24 h median");
        if (MarketParsers.Median(history, now - TimeSpan.FromDays(7)) is { } week) return (week, "7 d median");
        return (null, "24 h median");
    }
}

/// <summary>The sell dialog for one item: market prices, three quick prices plus a custom one, fees and warnings.</summary>
public sealed partial class SellViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _s;
    private readonly CancellationTokenSource _cts = new();
    private WalletInfo? _wallet;
    private OrderBook? _book;
    private decimal? _median;
    private string _medianLabel = "24 h median";
    private long _receive;
    private bool _sync;
    private bool _customByReceive;

    public SellViewModel(AppServices s, InventoryItem item, PriceQuote? quote)
    {
        _s = s;
        Item = item;
        IconUrl = item.IconUrl;
        Name = item.Name;
        Wear = ItemText.WearLong(item);
        RarityHex = item.RarityHex;
        RarityLine = item.RarityLine;
        ValueWarning = ItemText.ValueWarning(item) ?? "";
        ReferenceText = quote is null ? "" : $"App price {Money.Format(quote)} · {PriceSourceIds.TableName(quote.SourceId)}";
        Choices =
        [
            new PriceChoice("undercut", "Undercut lowest") { Hint = "1 cent under the cheapest listing" },
            new PriceChoice("buy", "Highest buy order") { Hint = "Sells right away to the best buy order" },
            new PriceChoice("median", "24 h median") { Hint = "Typical sale price in the last 24 hours" },
        ];
        Custom = new PriceChoice("custom", "Custom price") { Available = true, Hint = "Type what the buyer pays or what you get" };
        _currency = s.Market.CurrencyIso;
        Compare = new SellCompareViewModel(SellAdvisor.For(s.Prices, item),
            "Compared at the app's latest prices. This window lists on the Steam Market only; Skinport sales are made on skinport.com.");
        s.Market.Changed += OnMarketChanged;
        UpdateMode();
    }

    public InventoryItem Item { get; }
    public string IconUrl { get; }
    public string Name { get; }
    public string Wear { get; }
    public string RarityHex { get; }
    public string RarityLine { get; }
    public string ValueWarning { get; }
    public string ReferenceText { get; }
    public IReadOnlyList<PriceChoice> Choices { get; }
    public PriceChoice Custom { get; }
    public SellCompareViewModel Compare { get; }
    public ListingRow? Listing { get; private set; }

    public event EventHandler? CloseRequested;

    /// <summary>sell, listed, protected or locked.</summary>
    [ObservableProperty] private string _mode = "sell";
    /// <summary>loading, ready, selling, done or failed.</summary>
    [ObservableProperty] private string _stage = "loading";
    [ObservableProperty] private string _currency;
    [ObservableProperty] private string _statusText = "Loading market prices…";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _walletText = "";

    [ObservableProperty] private string _lowestText = "—";
    [ObservableProperty] private string _lowestSub = "";
    [ObservableProperty] private string _buyText = "—";
    [ObservableProperty] private string _buySub = "";
    [ObservableProperty] private string _medianText = "—";
    [ObservableProperty] private string _medianSub = "";
    [ObservableProperty] private string _medianTitle = "24 h median";
    [ObservableProperty] private IReadOnlyList<PricePoint>? _history;
    [ObservableProperty] private string _historyNote = "Loading";
    [ObservableProperty] private decimal? _marker;

    [ObservableProperty] private PriceChoice? _selected;
    [ObservableProperty] private string _customBuyer = "";
    [ObservableProperty] private string _customReceive = "";
    [ObservableProperty] private string _buyerPaysText = "—";
    [ObservableProperty] private string _receiveText = "—";
    [ObservableProperty] private string _feeText = "";
    [ObservableProperty] private IReadOnlyList<string> _warnings = [];
    [ObservableProperty] private bool _canList;
    [ObservableProperty] private string _listButtonText = "List on market";

    [ObservableProperty] private string _infoTitle = "";
    [ObservableProperty] private string _infoText = "";
    [ObservableProperty] private bool _confirmCancel;
    [ObservableProperty] private bool _canCancelListing;

    [ObservableProperty] private string _doneTitle = "";
    [ObservableProperty] private string _doneText = "";

    private string Fmt(long cents) => SellText.Cents(cents, Currency);

    private void OnMarketChanged(object? sender, EventArgs e)
    {
        if (Stage is "selling" or "done") return;
        UpdateMode();
        Recompute();
    }

    private void UpdateMode()
    {
        var sell = _s.Market.Evaluate(Item);
        Listing = _s.Market.ListingFor(Item.AssetId);
        CanCancelListing = Listing is not null;
        switch (sell.State)
        {
            case SellState.Listed:
                Mode = "listed";
                var pending = Listing?.State == ListingState.Pending;
                InfoTitle = pending ? "Waiting for your confirmation" : "Listed on the Steam Market";
                InfoText = Listing is { } l
                    ? $"Buyer pays {SellText.Cents(l.BuyerCents, l.Currency)} · you get {SellText.Cents(l.ReceiveCents, l.Currency)} · listed {When.Format(l.FirstSeen)}."
                      + (pending ? " Accept it in the Steam Mobile app (or the email from Steam) to put it on sale." : "")
                    : "Listed moments ago. It shows up here once Steam reports it.";
                break;
            case SellState.Protected:
                Mode = "protected";
                InfoTitle = "Trade protected";
                InfoText = sell.Until is { } until
                    ? $"You can list it in {When.Left(until - DateTimeOffset.UtcNow)} ({When.Format(until)}, {until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)})."
                      + (sell.Estimated ? " Estimated from when the app first saw it; Steam didn't say." : "")
                    : "Items you get by trade or from the market can't be listed for 7 days.";
                break;
            case SellState.NotMarketable:
                Mode = "locked";
                InfoTitle = "Can't be sold on the market";
                InfoText = "Steam doesn't allow this item on the Community Market.";
                break;
            default:
                Mode = "sell";
                break;
        }
    }

    public async Task LoadAsync()
    {
        var ct = _cts.Token;
        Stage = "loading";
        StatusText = "Loading market prices…";
        try
        {
            _wallet = await _s.Market.EnsureWalletAsync(ct);
            if (_wallet is null)
            {
                Stage = "failed";
                StatusText = "";
                Error = _s.Market.LastError.Length > 0 ? _s.Market.LastError : "Couldn't load your Steam Wallet.";
                return;
            }
            Currency = _wallet.CurrencyIso;
            WalletText = "Wallet " + Fmt(_wallet.Balance) + (_wallet.DelayedBalance > 0 ? $" (+{Fmt(_wallet.DelayedBalance)} pending)" : "");

            var bookTask = _s.Market.GetOrderBookAsync(Item.MarketHashName, _wallet.CurrencyCode, ct);
            var historyTask = _s.Market.GetPriceHistoryAsync(Item.MarketHashName, ct);
            _book = await bookTask;
            var history = await historyTask;
            var now = DateTimeOffset.UtcNow;

            History = history;
            HistoryNote = history is null ? "Price history unavailable" : "No sales in the last 30 days";
            if (_book is { } b)
            {
                LowestText = b.LowestSell is { } low ? Fmt(low) : "None";
                LowestSub = b.SellCount == 1 ? "1 listing" : $"{b.SellCount:N0} listings";
                BuyText = b.HighestBuy is { } hb ? Fmt(hb) : "None";
                BuySub = b.BuyCount == 1 ? "1 buy order" : $"{b.BuyCount:N0} buy orders";
            }
            else
            {
                LowestText = BuyText = "—";
                LowestSub = BuySub = "Steam didn't answer";
            }
            (_median, _medianLabel) = SellText.MedianWithFallback(history, now);
            MedianTitle = _medianLabel;
            MedianText = _median is { } m ? Money.Format(m, Currency) : "—";
            var sold = history?.Where(p => p.At >= now - (_medianLabel.StartsWith("7") ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24))).Sum(p => p.Volume) ?? 0;
            MedianSub = history is null ? "needs sign-in" : _median is null ? "no recent sales" : $"{sold:N0} sold";

            var (undercut, buy, _) = MarketService.Suggestions(_book, history, now);
            long? median = _median is { } md ? Math.Max(3, MarketFees.ToCents(md)) : null;
            SetChoice(Choices[0], undercut);
            SetChoice(Choices[1], buy);
            SetChoice(Choices[2], median);
            Choices[2].Hint = _medianLabel.StartsWith("7") ? "Typical sale price in the last 7 days (no sales in 24 h)" : "Typical sale price in the last 24 hours";

            var pick = Choices.FirstOrDefault(c => c.Available) ?? Custom;
            if (pick == Custom && Custom.Buyer is null && _book?.LowestSell is null && history is null) Error = "Steam has no prices for this item right now.";
            Select(pick);
            Stage = "ready";
            StatusText = "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Stage = "failed";
            StatusText = "";
            Error = "Couldn't load market prices: " + ex.Message;
        }
        Recompute();
    }

    private void SetChoice(PriceChoice c, long? buyer)
    {
        c.Buyer = buyer is >= 3 ? buyer : null;
        c.Available = c.Buyer is not null;
        if (c.Buyer is { } b && _wallet is { } w)
        {
            var fee = MarketFees.ForReceive(MarketFees.ForBuyerPrice(b, w).Receive, w);
            c.Buyer = fee.BuyerPays;
            c.PriceText = Fmt(fee.BuyerPays);
            c.ReceiveText = "you get " + Fmt(fee.Receive);
        }
        else
        {
            c.PriceText = "—";
            c.ReceiveText = "not available";
        }
    }

    [RelayCommand]
    private void Select(PriceChoice? choice)
    {
        if (choice is null || (!choice.Available && choice != Custom)) return;
        foreach (var c in Choices) c.IsSelected = c == choice;
        Custom.IsSelected = choice == Custom;
        Selected = choice;
        if (choice != Custom && choice.Buyer is { } b && _wallet is { } w)
        {
            _sync = true;
            CustomBuyer = SellText.Plain(b);
            CustomReceive = SellText.Plain(MarketFees.ForBuyerPrice(b, w).Receive);
            _sync = false;
        }
        Recompute();
    }

    partial void OnCustomBuyerChanged(string value)
    {
        if (_sync) return;
        _customByReceive = false;
        var cents = SellText.ParseCents(value);
        Custom.Buyer = cents;
        if (_wallet is { } w && cents is { } c)
        {
            _sync = true;
            CustomReceive = SellText.Plain(MarketFees.ForBuyerPrice(c, w).Receive);
            _sync = false;
        }
        Select(Custom);
    }

    partial void OnCustomReceiveChanged(string value)
    {
        if (_sync) return;
        _customByReceive = true;
        var cents = SellText.ParseCents(value);
        Custom.Buyer = cents is { } r && _wallet is { } w ? MarketFees.ForReceive(r, w).BuyerPays : null;
        if (Custom.Buyer is { } b)
        {
            _sync = true;
            CustomBuyer = SellText.Plain(b);
            _sync = false;
        }
        Select(Custom);
    }

    private void Recompute()
    {
        var warnings = new List<string>();
        var w = _wallet;
        long? receive = null;
        if (w is not null && Selected is { } sel)
        {
            if (sel == Custom && _customByReceive) receive = SellText.ParseCents(CustomReceive);
            else if (sel.Buyer is { } b) receive = MarketFees.ForBuyerPrice(b, w).Receive;
        }
        if (w is null || receive is not { } rc || rc < 1)
        {
            _receive = 0;
            BuyerPaysText = ReceiveText = "—";
            FeeText = Selected == Custom && Stage == "ready" ? "Enter a price of at least " + Fmt(3) : "";
            Marker = null;
            CanList = false;
            ListButtonText = "List on market";
            Warnings = warnings;
            return;
        }

        var fee = MarketFees.ForReceive(rc, w);
        _receive = fee.Receive;
        BuyerPaysText = Fmt(fee.BuyerPays);
        ReceiveText = Fmt(fee.Receive);
        FeeText = $"Steam fee {Fmt(fee.SteamFee)} · CS2 fee {Fmt(fee.PublisherFee)}";
        Marker = MarketFees.FromCents(fee.BuyerPays);
        ListButtonText = "List for " + Fmt(fee.BuyerPays);

        var low = _book?.LowestSell;
        var high = _book?.HighestBuy;
        if (low is { } lo && fee.BuyerPays < lo * 0.8m)
            warnings.Add($"{Pct(fee.BuyerPays, lo)}% below the cheapest listing ({Fmt(lo)}).");
        if (_median is { } med && MarketFees.ToCents(med) is var mc && mc > 0 && fee.BuyerPays < mc * 0.75m)
            warnings.Add($"{Pct(fee.BuyerPays, mc)}% below the {_medianLabel} ({Fmt(mc)}).");
        if (low is { } lo2 && fee.BuyerPays > lo2 * 1.5m)
            warnings.Add("Well above the cheapest listing. It may take a long time to sell.");
        if (Selected == Custom && high is { } hb && fee.BuyerPays <= hb)
            warnings.Add("At or below the highest buy order, so it sells right away.");
        if (w.MaxBalance > 0 && w.Balance + w.DelayedBalance + fee.Receive > w.MaxBalance)
            warnings.Add($"Your Steam Wallet holds at most {Fmt(w.MaxBalance)}. Steam may refuse a sale that takes it over.");
        Warnings = warnings;
        CanList = Mode == "sell" && Stage == "ready";
    }

    private static int Pct(long price, long reference) => (int)Math.Round((1 - price / (decimal)reference) * 100);

    public bool ShowSell => Mode == "sell" && Stage != "done";
    public bool ShowInfo => Mode != "sell" && Stage != "done";
    public bool ShowDone => Stage == "done";
    public bool ShowListed => Mode == "listed" && Stage != "done";
    public string CancelText => ConfirmCancel ? "Yes, remove the listing" : "Cancel listing";

    partial void OnModeChanged(string value)
    {
        CanList = value == "sell" && Stage == "ready" && _receive > 0;
        RaiseShown();
    }

    partial void OnStageChanged(string value)
    {
        CanList = Mode == "sell" && value == "ready" && _receive > 0;
        RaiseShown();
    }

    partial void OnConfirmCancelChanged(bool value) => OnPropertyChanged(nameof(CancelText));

    private void RaiseShown()
    {
        OnPropertyChanged(nameof(ShowSell));
        OnPropertyChanged(nameof(ShowInfo));
        OnPropertyChanged(nameof(ShowDone));
        OnPropertyChanged(nameof(ShowListed));
    }

    [RelayCommand]
    private async Task ListAsync()
    {
        if (!CanList || _receive < 1) return;
        Stage = "selling";
        StatusText = "Listing on the Steam Market…";
        Error = "";
        // Not cancellable: once the request is sent, Steam may create the listing whether or not we wait.
        var r = await _s.Market.SellAsync(Item, _receive, CancellationToken.None);
        StatusText = "";
        if (r.Success)
        {
            Stage = "done";
            DoneTitle = r.NeedsMobileConfirmation || r.NeedsEmailConfirmation ? "Confirm the listing" : "Listed";
            DoneText = r.Message + (r.NeedsMobileConfirmation || r.NeedsEmailConfirmation
                ? " It isn't for sale until you confirm. You can cancel it from the Market page."
                : " You can cancel it from the Market page.");
            _ = RefreshLaterAsync();
        }
        else
        {
            Stage = "ready";
            Error = r.Message;
        }
        Recompute();
    }

    [RelayCommand]
    private async Task CancelListingAsync()
    {
        if (Listing is not { } l) return;
        if (!ConfirmCancel)
        {
            ConfirmCancel = true;
            return;
        }
        Stage = "selling";
        StatusText = "Removing the listing…";
        Error = "";
        var (ok, message) = await _s.Market.CancelAsync(l, CancellationToken.None);
        StatusText = "";
        ConfirmCancel = false;
        if (ok)
        {
            Stage = "done";
            DoneTitle = "Listing removed";
            DoneText = message;
            _ = RefreshLaterAsync();
        }
        else
        {
            Stage = "ready";
            Error = message;
        }
    }

    [RelayCommand]
    private async Task RefreshListingAsync()
    {
        StatusText = "Checking your listings…";
        await _s.Market.RefreshAsync(_cts.Token);
        StatusText = "";
        if (_s.Market.LastError.Length > 0) Error = _s.Market.LastError;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async Task RefreshLaterAsync()
    {
        try
        {
            await Task.Delay(2500);
            await _s.Market.RefreshAsync(CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _s.Market.Changed -= OnMarketChanged;
        _cts.Cancel();
    }
}

public sealed partial class BulkRow(InventoryItem item) : ObservableObject
{
    public InventoryItem Item { get; } = item;
    public string IconUrl => Item.IconUrl;
    public string Name => Item.Name;
    public string Wear => ItemText.WearLong(Item);
    public string RarityHex => Item.RarityHex;
    public OrderBook? Book { get; set; }
    public IReadOnlyList<PricePoint>? History { get; set; }
    public bool HistoryLoaded { get; set; }
    public long Receive { get; set; }
    public long BuyerPays { get; set; }

    [ObservableProperty] private string _lowestText = "…";
    [ObservableProperty] private string _buyText = "…";
    [ObservableProperty] private string _priceText = "…";
    [ObservableProperty] private string _receiveText = "";
    [ObservableProperty] private string _status = "";
    /// <summary>ok, warn, busy or empty.</summary>
    [ObservableProperty] private string _statusKind = "";
}

/// <summary>Lists several items with one price rule, one request at a time.</summary>
public sealed partial class BulkSellViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(2.5);

    private readonly AppServices _s;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action? _onListed;
    private WalletInfo? _wallet;
    private bool _stop;

    public BulkSellViewModel(AppServices s, IReadOnlyList<InventoryItem> items, Action? onListed)
    {
        _s = s;
        _onListed = onListed;
        foreach (var i in items) Rows.Add(new BulkRow(i));
        _currency = s.Market.CurrencyIso;
        Title = items.Count == 1 ? "Sell 1 item" : $"Sell {items.Count} items";
    }

    public ObservableCollection<BulkRow> Rows { get; } = new();
    public string Title { get; }
    public event EventHandler? CloseRequested;

    /// <summary>undercut, buy or median.</summary>
    [ObservableProperty] private string _rule = "undercut";
    /// <summary>loading, ready, selling, done or failed.</summary>
    [ObservableProperty] private string _stage = "loading";
    [ObservableProperty] private string _currency;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _totalBuyer = "—";
    [ObservableProperty] private string _totalReceive = "—";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _canList;
    [ObservableProperty] private string _listButtonText = "List items";
    [ObservableProperty] private string _doneText = "";
    [ObservableProperty] private string _ruleHint = "1 cent under the cheapest listing of each item";

    private string Fmt(long cents) => SellText.Cents(cents, Currency);

    public async Task LoadAsync()
    {
        var ct = _cts.Token;
        try
        {
            StatusText = "Loading your Steam Wallet…";
            _wallet = await _s.Market.EnsureWalletAsync(ct);
            if (_wallet is null)
            {
                Stage = "failed";
                StatusText = "";
                Error = _s.Market.LastError.Length > 0 ? _s.Market.LastError : "Couldn't load your Steam Wallet.";
                return;
            }
            Currency = _wallet.CurrencyIso;
            var names = Rows.Select(r => r.Item.MarketHashName).Distinct(StringComparer.Ordinal).ToList();
            var books = new Dictionary<string, OrderBook?>(StringComparer.Ordinal);
            for (var i = 0; i < names.Count; i++)
            {
                StatusText = $"Loading market prices {i + 1}/{names.Count}…";
                Progress = (double)i / names.Count;
                books[names[i]] = await _s.Market.GetOrderBookAsync(names[i], _wallet.CurrencyCode, ct);
                foreach (var r in Rows.Where(r => r.Item.MarketHashName == names[i])) FillBook(r, books[names[i]]);
            }
            Progress = 1;
            StatusText = "";
            Stage = "ready";
            await ApplyRuleAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Stage = "failed";
            StatusText = "";
            Error = "Couldn't load market prices: " + ex.Message;
        }
    }

    private void FillBook(BulkRow r, OrderBook? book)
    {
        r.Book = book;
        r.LowestText = book?.LowestSell is { } low ? Fmt(low) : book is null ? "—" : "None";
        r.BuyText = book?.HighestBuy is { } hb ? Fmt(hb) : book is null ? "—" : "None";
    }

    partial void OnRuleChanged(string value)
    {
        RuleHint = value switch
        {
            "buy" => "Each item sells right away to its highest buy order",
            "median" => "The typical sale price of each item (24 h, or 7 d if it didn't sell)",
            _ => "1 cent under the cheapest listing of each item",
        };
        if (Stage == "ready") _ = ApplyRuleAsync();
    }

    private async Task ApplyRuleAsync()
    {
        if (_wallet is not { } w) return;
        var ct = _cts.Token;
        if (Rule == "median")
        {
            var missing = Rows.Where(r => !r.HistoryLoaded).Select(r => r.Item.MarketHashName).Distinct(StringComparer.Ordinal).ToList();
            for (var i = 0; i < missing.Count && Rule == "median"; i++)
            {
                StatusText = $"Loading sale history {i + 1}/{missing.Count}…";
                CanList = false;
                List<PricePoint>? h;
                try { h = await _s.Market.GetPriceHistoryAsync(missing[i], ct); }
                catch (OperationCanceledException) { return; }
                foreach (var r in Rows.Where(r => r.Item.MarketHashName == missing[i]))
                {
                    r.History = h;
                    r.HistoryLoaded = true;
                }
            }
            StatusText = "";
            if (Rule != "median") return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var r in Rows)
        {
            if (r.StatusKind == "ok") continue;
            var (undercut, buy, _) = MarketService.Suggestions(r.Book, null, now);
            long? buyer = Rule switch
            {
                "buy" => buy,
                "median" => SellText.MedianWithFallback(r.History, now).Price is { } m ? Math.Max(3, MarketFees.ToCents(m)) : null,
                _ => undercut,
            };
            if (buyer is { } b && b >= 3)
            {
                var fee = MarketFees.ForReceive(MarketFees.ForBuyerPrice(b, w).Receive, w);
                r.Receive = fee.Receive;
                r.BuyerPays = fee.BuyerPays;
                r.PriceText = Fmt(fee.BuyerPays);
                r.ReceiveText = Fmt(fee.Receive);
                r.Status = "";
                r.StatusKind = "";
            }
            else
            {
                r.Receive = r.BuyerPays = 0;
                r.PriceText = "—";
                r.ReceiveText = "";
                r.Status = Rule == "buy" ? "No buy orders" : Rule == "median" && r.History is null ? "No history" : "No price";
                r.StatusKind = "warn";
            }
        }
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var ready = Rows.Where(r => r.Receive > 0 && r.StatusKind != "ok").ToList();
        TotalBuyer = ready.Count == 0 ? "—" : Fmt(ready.Sum(r => r.BuyerPays));
        TotalReceive = ready.Count == 0 ? "—" : Fmt(ready.Sum(r => r.Receive));
        var skipped = Rows.Count(r => r.Receive == 0 && r.StatusKind != "ok");
        CountText = skipped == 0 ? $"{ready.Count} to list" : $"{ready.Count} to list · {skipped} without a price";
        ListButtonText = ready.Count == 1 ? "List 1 item" : $"List {ready.Count} items";
        CanList = Stage == "ready" && ready.Count > 0;
    }

    [RelayCommand]
    private async Task ListAsync()
    {
        if (!CanList) return;
        var todo = Rows.Where(r => r.Receive > 0 && r.StatusKind != "ok").ToList();
        Stage = "selling";
        CanList = false;
        Error = "";
        _stop = false;
        int ok = 0, failed = 0, confirm = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            var r = todo[i];
            if (_stop)
            {
                r.Status = "Skipped";
                r.StatusKind = "";
                continue;
            }
            StatusText = $"Listing {i + 1}/{todo.Count}…";
            Progress = (double)i / todo.Count;
            r.Status = "Listing…";
            r.StatusKind = "busy";
            // Not cancellable: once the request is sent, Steam may create the listing whether or not we wait.
            var result = await _s.Market.SellAsync(r.Item, r.Receive, CancellationToken.None);
            if (result.Success)
            {
                ok++;
                if (result.NeedsMobileConfirmation || result.NeedsEmailConfirmation) confirm++;
                r.Status = result.NeedsMobileConfirmation || result.NeedsEmailConfirmation ? "Listed · confirm" : "Listed";
                r.StatusKind = "ok";
            }
            else
            {
                failed++;
                r.Status = result.Message;
                r.StatusKind = "warn";
                if (result.Message.Contains("sign", StringComparison.OrdinalIgnoreCase) || result.Message.Contains("rate", StringComparison.OrdinalIgnoreCase)
                    || result.Message.Contains("reach", StringComparison.OrdinalIgnoreCase))
                {
                    Error = result.Message;
                    _stop = true;
                }
            }
            if (i + 1 < todo.Count && !_stop) await Task.Delay(Spacing);
        }
        Progress = 1;
        StatusText = "";
        Stage = "done";
        DoneText = (ok == 1 ? "1 item listed" : $"{ok} items listed") + (failed > 0 ? $", {failed} failed" : "") + "."
            + (confirm > 0 ? " Open the Steam Mobile app and accept them under Confirmations (Confirm all works). They aren't for sale until you do." : "");
        if (ok > 0)
        {
            _onListed?.Invoke();
            _ = RefreshLaterAsync();
        }
    }

    [RelayCommand]
    private void Stop() => _stop = true;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async Task RefreshLaterAsync()
    {
        try
        {
            await Task.Delay(2500);
            await _s.Market.RefreshAsync(CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    public bool IsSelling => Stage == "selling";

    partial void OnStageChanged(string value) => OnPropertyChanged(nameof(IsSelling));

    public void Dispose()
    {
        _stop = true;
        _cts.Cancel();
    }
}
