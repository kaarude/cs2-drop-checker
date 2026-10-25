using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Steam;
using Cs2DropChecker.Core.Util;
using Cs2DropChecker.Core.Valuation;

namespace Cs2DropChecker.App.ViewModels;

/// <summary>Header (identity, KPIs, value bar) plus tile grid, shared by the Inventory and Lookup pages.</summary>
public abstract partial class ProfileInventoryViewModel : ObservableObject
{
    protected InventorySnapshot? Snapshot;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _initials = "";
    [ObservableProperty] private string _avatarUrl = "";
    [ObservableProperty] private string _idLine = "";
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string _hiddenNote = "";
    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private string _valueSource = "";
    [ObservableProperty] private string _itemsText = "—";
    [ObservableProperty] private string _pricedText = "";
    [ObservableProperty] private string _bestName = "—";
    [ObservableProperty] private string _bestPrice = "";
    [ObservableProperty] private bool _bestIsStar;
    [ObservableProperty] private IReadOnlyList<ValueGroup> _groups = InventoryValuation.EmptyGroups();
    [ObservableProperty] private bool _hasData;

    public ItemGridViewModel Grid { get; } = new();

    protected void FillHeader(InventoryValue value, string? sourceId, string currency)
    {
        ValueText = Money.Format(value.Total, currency);
        ValueSource = sourceId is null ? "no prices yet" : PriceSourceIds.TableName(sourceId) switch
        {
            "Steam 24h" => "Steam 24 h median",
            "Steam Market" => "Steam 24 h median",
            var other => other,
        };
        ItemsText = value.ItemCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        PricedText = $"{value.PricedCount} priced";
        if (value.MostValuable is { } best)
        {
            BestName = best.Name;
            BestPrice = Money.Format(value.MostValuablePrice, currency);
            BestIsStar = best.IsStar;
        }
        else
        {
            BestName = "—";
            BestPrice = "";
            BestIsStar = false;
        }
        Groups = value.Groups;
    }

    protected void ClearHeader()
    {
        Snapshot = null;
        Name = Initials = AvatarUrl = IdLine = StatusLine = HiddenNote = PricedText = BestPrice = ValueSource = "";
        ValueText = ItemsText = BestName = "—";
        BestIsStar = false;
        Groups = InventoryValuation.EmptyGroups();
        HasData = false;
        Grid.Clear();
    }

    public abstract void Tick();
}

public sealed partial class InventoryViewModel(AppServices s) : ProfileInventoryViewModel
{
    private InventorySnapshot? _shown;

    public void Refresh(bool pricesOnly)
    {
        var w = s.Watcher;
        var snap = w.Current;
        var settings = s.Settings;
        Name = settings.ProfileName.Length > 0 ? settings.ProfileName : settings.SteamId;
        Initials = ItemText.Initials(Name);
        AvatarUrl = settings.AvatarUrl;
        IdLine = settings.SteamId64 == 0 ? "" : "SteamID64 " + SteamIdMask.Mask(settings.SteamId64);
        if (snap is null)
        {
            HasData = false;
            if (_shown is not null) Grid.Clear();
            _shown = null;
            StatusLine = w.State == WatchState.Private ? "Inventory private" : "Loading inventory…";
            return;
        }
        HasData = true;
        Snapshot = snap;
        FillHeader(w.Value, w.ValueSourceId, settings.Currency);
        HiddenNote = snap.HiddenCount > 0
            ? $"{snap.HiddenCount} item{(snap.HiddenCount == 1 ? "" : "s")} you got by trade or purchase in the last 7 days {(snap.HiddenCount == 1 ? "is" : "are")} hidden by Steam until the hold ends. Sign in to see them now."
            : "";
        if (!ReferenceEquals(snap, _shown) || !pricesOnly)
        {
            if (!ReferenceEquals(snap, _shown)) Grid.SetItems(snap.Items, s.Prices.GetCached);
            else Grid.RefreshPrices(s.Prices.GetCached);
            _shown = snap;
        }
        else
        {
            Grid.RefreshPrices(s.Prices.GetCached);
        }
        Tick();
    }

    public override void Tick()
    {
        if (Snapshot is null) return;
        StatusLine = (Snapshot.SignedIn ? "Signed in · includes trade-protected items" : "Inventory public") + " · fetched " + When.Ago(Snapshot.TakenAt);
    }
}

public sealed partial class LookupViewModel(AppServices s) : ProfileInventoryViewModel
{
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _error = "";

    [RelayCommand]
    private async Task LookupAsync()
    {
        var q = Query.Trim();
        if (q.Length == 0) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Busy = true;
        Error = "";
        try
        {
            var profile = await s.Profiles.ResolveAsync(q, cts.Token);
            var r = await s.Fetcher.FetchAsync(s.HttpTransport, profile.SteamId, [InventoryFetcher.MainContext], cts.Token);
            if (cts.IsCancellationRequested) return;
            if (r.Status != FetchStatus.Ok || r.Snapshot is null)
            {
                Error = r.Status switch
                {
                    FetchStatus.Private => $"{profile.Name}'s inventory is private. Only public inventories can be looked up.",
                    FetchStatus.NotFound => $"{profile.Name} has no CS2 inventory.",
                    FetchStatus.RateLimited => "Steam is rate-limiting requests right now. Try again in a minute.",
                    _ => r.Message,
                };
                return;
            }
            ClearHeader();
            var snap = r.Snapshot;
            Snapshot = snap;
            Name = profile.Name;
            Initials = ItemText.Initials(profile.Name);
            AvatarUrl = profile.AvatarUrl;
            IdLine = "SteamID64 " + SteamIdMask.Mask(profile.SteamId);
            var counts = new Dictionary<string, int>();
            var value = InventoryValuation.Compute(snap.Items, i =>
            {
                var pq = s.Prices.GetCached(i.MarketHashName);
                if (pq is null) return null;
                counts[pq.SourceId] = counts.GetValueOrDefault(pq.SourceId) + 1;
                return pq.Price;
            });
            FillHeader(value, counts.Count == 0 ? null : counts.MaxBy(kv => kv.Value).Key, s.Settings.Currency);
            HiddenNote = snap.HiddenCount > 0
                ? $"{snap.HiddenCount} item{(snap.HiddenCount == 1 ? "" : "s")} they got by trade or purchase in the last 7 days {(snap.HiddenCount == 1 ? "is" : "are")} hidden by Steam."
                : "Items they got by trade or purchase in the last 7 days are hidden by Steam.";
            Grid.SetItems(snap.Items, s.Prices.GetCached);
            HasData = true;
            Tick();
        }
        catch (SteamLookupException ex)
        {
            Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = "Lookup failed: " + ex.Message;
        }
        finally
        {
            if (_cts == cts) Busy = false;
        }
    }

    [RelayCommand]
    public void Clear()
    {
        _cts?.Cancel();
        Busy = false;
        Query = "";
        Error = "";
        ClearHeader();
    }

    public override void Tick()
    {
        if (Snapshot is null) return;
        StatusLine = "Inventory public · fetched " + When.Ago(Snapshot.TakenAt);
    }
}
