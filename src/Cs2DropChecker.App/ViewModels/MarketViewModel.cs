using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Storage;

namespace Cs2DropChecker.App.ViewModels;

public sealed partial class ListingRowViewModel(ListingRow l) : ObservableObject
{
    public ListingRow Listing { get; } = l;
    public string IconUrl => Listing.Item?.IconUrl ?? "";
    public string Name => Listing.Item?.Name is { Length: > 0 } n ? n : Listing.Name.Length > 0 ? Listing.Name : Listing.MarketHashName;
    public string Wear => Listing.Item is { } i ? ItemText.WearLong(i) : "";
    public string RarityHex => Listing.Item?.RarityHex ?? "";
    public string BuyerText => SellText.Cents(Listing.BuyerCents, Listing.Currency);
    public string ReceiveText => SellText.Cents(Listing.ReceiveCents, Listing.Currency);
    public string ListedText => When.Format(Listing.FirstSeen);
    public bool Pending => Listing.State == ListingState.Pending;
    public string StateText => Pending ? "Confirm on phone" : "On sale";

    [ObservableProperty] private bool _confirmCancel;
    [ObservableProperty] private bool _busy;

    public string CancelText => ConfirmCancel ? "Remove it" : "Cancel";

    partial void OnConfirmCancelChanged(bool value) => OnPropertyChanged(nameof(CancelText));
}

public sealed class SaleRowViewModel
{
    public SaleRowViewModel(SaleRecord r, decimal? profit)
    {
        Sale = r;
        IconUrl = r.Item.IconUrl;
        Name = r.Item.Name.Length > 0 ? r.Item.Name : r.MarketHashName;
        Wear = ItemText.WearLong(r.Item);
        RarityHex = r.Item.RarityHex;
        When = Services.When.Format(r.SoldAt);
        How = r.ArrivalKind is { } k ? ArrivalKindText.Label(k) : "";
        ArrivalText = r.ArrivalPrice is { } a ? Money.Format(a, r.ArrivalCurrency ?? r.Currency) : "—";
        BuyerText = Money.Format(r.BuyerPays, r.Currency);
        ReceiveText = Money.Format(r.Receive, r.Currency);
        Profit = profit;
        ProfitText = profit is { } p ? (p >= 0 ? "+" : "−") + Money.Format(Math.Abs(p), r.Currency) : "—";
        Trend = profit is { } q ? q > 0.004m ? "up" : q < -0.004m ? "down" : "" : "";
    }

    public SaleRecord Sale { get; }
    public string IconUrl { get; }
    public string Name { get; }
    public string Wear { get; }
    public string RarityHex { get; }
    public string When { get; }
    public string How { get; }
    public string ArrivalText { get; }
    public string BuyerText { get; }
    public string ReceiveText { get; }
    public decimal? Profit { get; }
    public string ProfitText { get; }
    public string Trend { get; }
}

/// <summary>The Market page: wallet, live listings with cancel, and sales with profit against the price at arrival.</summary>
public sealed partial class MarketViewModel : ObservableObject
{
    private readonly AppServices _s;
    private bool _shown;

    public MarketViewModel(AppServices s)
    {
        _s = s;
        s.Market.Changed += (_, _) =>
        {
            if (_shown) Reload();
        };
        s.Market.Sold += (_, _) =>
        {
            if (_shown) Reload();
        };
    }

    [ObservableProperty] private bool _canSell;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _updatedText = "";
    [ObservableProperty] private string _walletText = "—";
    [ObservableProperty] private string _walletSub = "";
    [ObservableProperty] private string _activeText = "0";
    [ObservableProperty] private string _activeSub = "";
    [ObservableProperty] private string _pendingText = "0";
    [ObservableProperty] private string _pendingSub = "";
    [ObservableProperty] private string _soldText = "—";
    [ObservableProperty] private string _soldSub = "";
    [ObservableProperty] private string _soldTrend = "";
    [ObservableProperty] private IReadOnlyList<ListingRowViewModel> _listings = [];
    [ObservableProperty] private IReadOnlyList<SaleRowViewModel> _sales = [];
    [ObservableProperty] private bool _hasListings;
    [ObservableProperty] private bool _hasSales;

    /// <summary>Called when the page opens. Loads saved data at once and refreshes from Steam when it's stale.</summary>
    public void OnShown()
    {
        _shown = true;
        Reload();
        var m = _s.Market;
        if (m.CanSell && !m.Busy && (m.ListingsAt is null || DateTimeOffset.UtcNow - m.ListingsAt.Value > TimeSpan.FromMinutes(2)))
            _ = RefreshAsync();
    }

    public void OnHidden() => _shown = false;

    public void Tick() => UpdatedText = UpdatedLine();

    private string UpdatedLine()
    {
        var m = _s.Market;
        if (m.Busy) return "Checking Steam…";
        if (!m.CanSell) return "Not signed in";
        return m.ListingsAt is { } at ? "Updated " + When.Ago(at) : "Not loaded yet";
    }

    public void Reload()
    {
        var m = _s.Market;
        CanSell = m.CanSell;
        Busy = m.Busy;
        Error = m.CanSell ? m.LastError : "";
        UpdatedText = UpdatedLine();
        var cur = m.CurrencyIso;

        if (m.Wallet is { } w)
        {
            WalletText = SellText.Cents(w.Balance, w.CurrencyIso);
            WalletSub = w.DelayedBalance > 0 ? "+" + SellText.Cents(w.DelayedBalance, w.CurrencyIso) + " pending" : "Steam Wallet · " + w.CurrencyIso;
        }
        else
        {
            WalletText = "—";
            WalletSub = m.CanSell ? "Loading" : "Sign in to see it";
        }

        var old = Listings.Where(r => r.ConfirmCancel || r.Busy).ToDictionary(r => r.Listing.ListingId, r => r);
        var rows = m.Listings.OrderByDescending(l => l.FirstSeen).Select(l =>
        {
            var row = new ListingRowViewModel(l);
            if (old.TryGetValue(l.ListingId, out var prev))
            {
                row.ConfirmCancel = prev.ConfirmCancel;
                row.Busy = prev.Busy;
            }
            return row;
        }).ToList();
        Listings = rows;
        HasListings = rows.Count > 0;
        var active = rows.Where(r => !r.Pending).ToList();
        var pending = rows.Count - active.Count;
        ActiveText = active.Count.ToString();
        ActiveSub = active.Count == 0 ? "Nothing on sale" : "You get " + Money.Format(active.Sum(r => Convert(r.Listing.ReceiveCents / 100m, r.Listing.Currency, cur) ?? 0), cur);
        PendingText = pending.ToString();
        PendingSub = pending == 0 ? "All confirmed" : "Accept in the Steam Mobile app";

        var sales = m.Sales().Select(r => new SaleRowViewModel(r, ProfitOf(r))).ToList();
        Sales = sales;
        HasSales = sales.Count > 0;
        var since = DateTimeOffset.UtcNow - TimeSpan.FromDays(30);
        var recent = sales.Where(r => r.Sale.SoldAt >= since).ToList();
        if (recent.Count == 0)
        {
            SoldText = Money.Format(0, cur);
            SoldSub = "No sales yet";
            SoldTrend = "";
        }
        else
        {
            SoldText = Money.Format(recent.Sum(r => Convert(r.Sale.Receive, r.Sale.Currency, cur) ?? 0), cur);
            var known = recent.Where(r => r.Profit is not null).ToList();
            var profit = known.Sum(r => Convert(r.Profit!.Value, r.Sale.Currency, cur) ?? 0);
            SoldSub = (recent.Count == 1 ? "1 sale" : $"{recent.Count} sales")
                + (known.Count > 0 ? " · " + (profit >= 0 ? "+" : "−") + Money.Format(Math.Abs(profit), cur) + " vs arrival" : "");
            SoldTrend = known.Count == 0 ? "" : profit > 0.004m ? "up" : profit < -0.004m ? "down" : "";
        }
    }

    private decimal? Convert(decimal value, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return value;
        return _s.Prices.Fx.Rate(from, to) is { } r ? value * r : null;
    }

    /// <summary>What the sale paid out minus what the item was worth when it arrived, in the sale currency.</summary>
    private decimal? ProfitOf(SaleRecord r)
    {
        if (r.ArrivalPrice is not { } a) return null;
        return Convert(a, r.ArrivalCurrency ?? r.Currency, r.Currency) is { } arrival ? r.Receive - arrival : null;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!_s.Market.CanSell)
        {
            Reload();
            return;
        }
        Busy = true;
        UpdatedText = "Checking Steam…";
        try { await _s.Market.RefreshAsync(CancellationToken.None, forceWallet: true); }
        catch (Exception) { }
        Reload();
    }

    [RelayCommand]
    private async Task CancelListingAsync(ListingRowViewModel? row)
    {
        if (row is null || row.Busy || !CanSell) return;
        if (!row.ConfirmCancel)
        {
            foreach (var r in Listings) r.ConfirmCancel = r == row;
            return;
        }
        row.Busy = true;
        var (ok, message) = await _s.Market.CancelAsync(row.Listing, CancellationToken.None);
        row.Busy = false;
        row.ConfirmCancel = false;
        Error = ok ? "" : message;
        if (ok) _ = RefreshLaterAsync();
        Reload();
        if (!ok) Error = message;
    }

    [RelayCommand]
    private void OpenOnSteam()
    {
        try { Process.Start(new ProcessStartInfo("https://steamcommunity.com/market/") { UseShellExecute = true }); }
        catch (Exception) { }
    }

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
}
