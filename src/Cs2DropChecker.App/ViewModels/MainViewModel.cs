using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Util;

namespace Cs2DropChecker.App.ViewModels;

public enum Page { Overview, Drops, Inventory, Lookup, Settings }

/// <summary>Shell state: navigation, title-bar status, sidebar footer and status bar.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _tick;
    private int _ticks;
    private bool _dropsDirty = true;

    public MainViewModel(AppServices s)
    {
        _s = s;
        Overview = new OverviewViewModel(s);
        Drops = new DropsViewModel(s);
        Inventory = new InventoryViewModel(s);
        Lookup = new LookupViewModel(s);
        Settings = new SettingsViewModel(s);
        _current = Overview;

        s.Watcher.StatusChanged += (_, _) => UpdateStatus();
        s.Watcher.InventoryChanged += (_, _) => OnInventoryChanged();
        s.Watcher.DropsAdded += (_, _) =>
        {
            _dropsDirty = true;
            Overview.Reload();
            if (Page == Page.Drops) LoadDrops();
            UpdateBadge();
        };
        s.Watcher.DropPriced += (_, e) =>
        {
            Overview.OnDropPriced(e.DropId, e.Quote);
            Drops.OnDropPriced(e.DropId, e.Quote);
        };
        s.SettingsChanged += (_, e) =>
        {
            var account = e.Old.SteamId != e.New.SteamId;
            var currency = e.Old.Currency != e.New.Currency;
            if (account || currency)
            {
                _dropsDirty = true;
                Overview.Reload();
                if (Page == Page.Drops) LoadDrops();
            }
            if (account) Lookup.Clear();
            Inventory.Refresh(pricesOnly: false);
            UpdateFooter();
            UpdateBadge();
        };
        s.Session.Changed += (_, _) => Settings.UpdateSession();

        _tick = new DispatcherTimer(DispatcherPriority.Background, s.Ui) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Tick();
        _tick.Start();

        UpdateStatus();
        UpdateFooter();
        UpdateBadge();
        Overview.Reload();
        Inventory.Refresh(pricesOnly: false);
    }

    public OverviewViewModel Overview { get; }
    public DropsViewModel Drops { get; }
    public InventoryViewModel Inventory { get; }
    public LookupViewModel Lookup { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private Page _page = Page.Overview;
    [ObservableProperty] private object _current;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusKind = "busy";
    [ObservableProperty] private string _statusTip = "";
    [ObservableProperty] private int _newDrops;
    [ObservableProperty] private string _footId = "";
    [ObservableProperty] private string _footPrices = "";
    [ObservableProperty] private string _footStartup = "";
    [ObservableProperty] private string _lastPollText = "";
    [ObservableProperty] private string _pricesText = "";
    [ObservableProperty] private string _refreshKey = "";

    partial void OnPageChanged(Page oldValue, Page newValue)
    {
        if (oldValue == Page.Lookup && newValue != Page.Lookup) Lookup.Clear();
        if (oldValue == Page.Settings && newValue != Page.Settings) Settings.CancelRecording();
        Current = newValue switch
        {
            Page.Drops => Drops,
            Page.Inventory => Inventory,
            Page.Lookup => Lookup,
            Page.Settings => Settings,
            _ => Overview,
        };
        switch (newValue)
        {
            case Page.Drops:
                if (_dropsDirty) LoadDrops();
                else Drops.RefreshNow();
                MarkDropsViewed();
                break;
            case Page.Settings:
                Settings.Load(onlyIfClean: true);
                break;
            case Page.Inventory:
                Inventory.Refresh(pricesOnly: true);
                break;
            case Page.Overview:
                Overview.RefreshValue();
                break;
        }
    }

    public void Navigate(Page page) => Page = page;

    [RelayCommand]
    private void RefreshNow() => _s.Watcher.PollNow();

    [RelayCommand]
    private void ViewAllDrops() => Page = Page.Drops;

    private void LoadDrops()
    {
        Drops.Reload();
        _dropsDirty = false;
    }

    private void MarkDropsViewed()
    {
        if (_s.Settings.SteamId64 == 0) return;
        _s.Mutate(x => x.LastViewedDropsAt = DateTimeOffset.UtcNow);
        NewDrops = 0;
    }

    private void OnInventoryChanged()
    {
        Inventory.Refresh(pricesOnly: true);
        Overview.RefreshValue();
        if (Page == Page.Drops) Drops.RefreshNow();
        UpdateStatusBar();
    }

    private void Tick()
    {
        _ticks++;
        UpdateStatus();
        UpdateStatusBar();
        if (Page == Page.Inventory) Inventory.Tick();
        if (Page == Page.Lookup) Lookup.Tick();
        if (_ticks % 60 == 0) Overview.RefreshWhen();
    }

    private void UpdateBadge()
    {
        var id = _s.Settings.SteamId64;
        if (id == 0 || Page == Page.Drops)
        {
            NewDrops = 0;
            return;
        }
        try { NewDrops = _s.Db.CountDrops(id, _s.Settings.LastViewedDropsAt); }
        catch (Exception) { NewDrops = 0; }
    }

    private void UpdateStatus()
    {
        var w = _s.Watcher;
        var next = w.NextPollAt is { } n ? When.Short(n - DateTimeOffset.UtcNow) : "";
        var cs2 = _s.Cs2.IsRunning;
        var (text, kind) = w.State switch
        {
            WatchState.NotSetUp => ("Not set up", "warn"),
            WatchState.Starting => ("Starting", "busy"),
            WatchState.Checking => ("Checking inventory", "busy"),
            WatchState.Watching => ("Watching" + (cs2 ? " · cs2.exe running" : "") + (next.Length > 0 ? " · next poll in " + next : ""), "ok"),
            WatchState.WaitingForCs2 => ("Idle · waits for cs2.exe", "idle"),
            WatchState.RateLimited => ("Steam rate limit" + (next.Length > 0 ? " · retry in " + next : ""), "warn"),
            WatchState.Private => ("Inventory private" + (next.Length > 0 ? " · retry in " + next : ""), "warn"),
            WatchState.NotFound => ("No CS2 inventory", "warn"),
            _ => ("Offline" + (next.Length > 0 ? " · retry in " + next : ""), "warn"),
        };
        StatusText = text;
        StatusKind = kind;
        StatusTip = w.Problem.Length > 0 ? w.Problem
            : w.State == WatchState.WaitingForCs2 ? "Checks start when CS2 runs. Change this in Settings > Watch."
            : "";
    }

    private void UpdateStatusBar()
    {
        var w = _s.Watcher;
        var items = w.Current?.Items.Count;
        LastPollText = $"Last poll {When.Ago(w.LastSuccessAt)}" + (items is { } c ? $" · {c} items" : "");
        var parts = new List<string>();
        foreach (var src in _s.Prices.Ordered())
        {
            parts.Add(src.Id switch
            {
                PriceSourceIds.SteamLive => "Steam live on new items",
                PriceSourceIds.SteamFeed => "Steam 24 h median " + (src.LastUpdated is { } a ? When.Ago(a) : "loading"),
                _ => "Skinport " + (src.LastUpdated is { } b ? When.Ago(b) : "loading"),
            });
        }
        PricesText = parts.Count == 0 ? "Prices: all sources off" : "Prices: " + string.Join(" · ", parts);
    }

    private void UpdateFooter()
    {
        var s = _s.Settings;
        FootId = s.SteamId64 == 0 ? "No profile yet" : "SteamID64 " + SteamIdMask.Mask(s.SteamId64);
        var names = _s.Prices.Ordered().Select(p => p.Id == PriceSourceIds.Skinport ? "Skinport" : "Steam").Distinct().ToList();
        FootPrices = s.Currency + (names.Count > 0 ? " · " + string.Join(" → ", names) : "");
        FootStartup = s.StartWithWindows ? "Starts with Windows" : "Manual start";
        RefreshKey = s.Hotkeys.RefreshNow;
    }
}
