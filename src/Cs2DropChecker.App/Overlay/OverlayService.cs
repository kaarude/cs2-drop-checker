using System.Windows.Threading;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Overlay;

/// <summary>
/// Decides what the in-game overlay shows: one card at a time from a queue, the total-value panel on request,
/// and short toggle feedback. The window only exists on screen while something is visible.
/// </summary>
public sealed class OverlayService : IDisposable
{
    private readonly AppServices _s;
    private readonly ILogger _log;
    private readonly OverlayViewModel _vm = new();
    private readonly LinkedList<CardViewModel> _queue = new();
    private readonly DispatcherTimer _frame;
    private OverlayWindow? _window;
    private OverlaySettings? _preview;
    private DateTimeOffset _cardStart;
    private TimeSpan _cardLength;
    private DateTimeOffset? _valueUntil;
    private DateTimeOffset? _toastUntil;
    private int _frames;

    public OverlayService(AppServices s, ILogger log)
    {
        _s = s;
        _log = log;
        _frame = new DispatcherTimer(DispatcherPriority.Normal, s.Ui) { Interval = TimeSpan.FromMilliseconds(100) };
        _frame.Tick += (_, _) => OnFrame();
        s.Watcher.DropsAdded += OnDropsAdded;
        s.Watcher.DropPriced += OnDropPriced;
        s.Watcher.NowSellable += OnNowSellable;
        s.Watcher.InventoryChanged += OnInventoryChanged;
        s.SettingsChanged += OnSettingsChanged;
    }

    private OverlaySettings Layout => _preview ?? _s.Settings.Overlay;

    public int QueuedCount => _queue.Count;

    private void OnDropsAdded(object? sender, DropsAddedEventArgs e)
    {
        if (!e.ShowCards) return;
        var o = _s.Settings.Overlay;
        if (!o.Enabled && !o.QueueWhileOff) return;
        foreach (var d in e.Drops.OrderByDescending(d => d.Item.IsStar))
            _queue.AddLast(new CardViewModel(d));
        _log.LogInformation("{Count} card(s) queued", e.Drops.Count);
        UpdateQueue();
        Pump();
    }

    /// <summary>Most valuable first, capped so a batch of estimated expiries can't flood the queue.</summary>
    private const int MaxSellableCards = 3;

    private void OnNowSellable(object? sender, SellableEventArgs e)
    {
        var o = _s.Settings.Overlay;
        if (!o.Enabled && !o.QueueWhileOff) return;
        var priced = e.Items.Select(i => (Item: i, Quote: _s.Prices.GetCached(i.MarketHashName)))
            .OrderByDescending(x => x.Quote?.Price ?? 0).Take(MaxSellableCards).ToList();
        foreach (var (item, quote) in priced)
        {
            var record = new DropRecord
            {
                Id = -2,
                SteamId = _s.Settings.SteamId64,
                SeenAt = DateTimeOffset.UtcNow,
                Kind = ArrivalKind.NewItem,
                Item = item,
            };
            var card = new CardViewModel(record, sellable: true);
            card.SetQuote(quote);
            _queue.AddLast(card);
        }
        _log.LogInformation("{Count} item(s) left trade protection, {Shown} card(s) queued", e.Items.Count, priced.Count);
        UpdateQueue();
        Pump();
    }

    private void OnDropPriced(object? sender, DropPricedEventArgs e)
    {
        if (_vm.Card is { } c && c.DropId == e.DropId && !c.IsTest) c.SetQuote(e.Quote);
        foreach (var q in _queue)
            if (q.DropId == e.DropId && !q.IsTest)
                q.SetQuote(e.Quote);
    }

    private void OnInventoryChanged(object? sender, EventArgs e)
    {
        if (_vm.ShowValue) _vm.Value.Fill(_s);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        var was = e.Old.Overlay;
        var now = e.New.Overlay;
        if (was.Enabled && !now.Enabled && _vm.Card is { IsTest: false } current)
        {
            _vm.Card = null;
            if (now.QueueWhileOff) _queue.AddFirst(current);
        }
        if (!now.Enabled && !now.QueueWhileOff) _queue.Clear();
        UpdateQueue();
        ApplyLayout();
        if (now.Enabled) Pump();
        else Refresh();
    }

    public void ToggleEnabled()
    {
        var on = !_s.Settings.Overlay.Enabled;
        _s.Mutate(x => x.Overlay.Enabled = on);
        var o = _s.Settings.Overlay;
        ShowToast(on ? "Overlay on" : o.QueueWhileOff ? "Overlay off · cards queued" : "Overlay off", _s.Settings.Hotkeys.ToggleOverlay, on);
    }

    public void ToggleValuePanel()
    {
        if (_vm.ShowValue)
        {
            _vm.ShowValue = false;
            _valueUntil = null;
        }
        else
        {
            _vm.Value.Fill(_s);
            _vm.ShowValue = true;
            _valueUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        }
        Refresh();
    }

    public void ShowToast(string text, string key, bool on)
    {
        _vm.Toast = text;
        _vm.ToastKey = key;
        _vm.ToastOn = on;
        _toastUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1.6);
        Refresh();
    }

    /// <summary>Shows a sample card right away using the given (possibly unsaved) layout.</summary>
    public void ShowTestCard(OverlaySettings preview)
    {
        _preview = preview;
        var w = _s.Watcher;
        var item = w.Value.MostValuable ?? w.Current?.Items.FirstOrDefault() ?? SampleItem();
        var quote = _s.Prices.GetCached(item.MarketHashName);
        var record = new DropRecord
        {
            Id = -1,
            SteamId = _s.Settings.SteamId64,
            SeenAt = DateTimeOffset.UtcNow,
            Kind = item.IsStar ? ArrivalKind.CaseOpened : ArrivalKind.WeeklyDrop,
            Item = item,
        };
        var card = new CardViewModel(record, test: true);
        card.SetQuote(quote);
        if (_vm.Card is { IsTest: false } current) _queue.AddFirst(current);
        ApplyLayout();
        StartCard(card);
    }

    private static InventoryItem SampleItem() => new()
    {
        AssetId = "0",
        MarketHashName = "★ Karambit | Doppler (Factory New)",
        Name = "★ Karambit | Doppler",
        Type = "★ Covert Knife",
        TypeName = "Knife",
        RarityName = "Covert",
        RarityColor = "eb4b4b",
        RarityTag = "Rarity_Ancient_Weapon",
        Exterior = "Factory New",
        Float = 0.0112,
    };

    private void Pump()
    {
        if (_vm.Card is not null) return;
        if (!_s.Settings.Overlay.Enabled || _queue.First is null)
        {
            Refresh();
            return;
        }
        var next = _queue.First.Value;
        _queue.RemoveFirst();
        StartCard(next);
    }

    private void StartCard(CardViewModel card)
    {
        card.UpdateAge();
        card.Remaining = 1;
        _vm.Card = card;
        _cardStart = DateTimeOffset.UtcNow;
        _cardLength = TimeSpan.FromSeconds(Math.Max(3, Layout.SecondsPerCard));
        UpdateQueue();
        Refresh();
        _window?.BringToTop();
    }

    private void UpdateQueue() => _vm.Queued = _queue.Count;

    private void OnFrame()
    {
        var now = DateTimeOffset.UtcNow;
        _frames++;
        if (_vm.Card is { } c)
        {
            var t = (now - _cardStart).TotalMilliseconds / _cardLength.TotalMilliseconds;
            if (t >= 1)
            {
                _vm.Card = null;
                if (c.IsTest)
                {
                    _preview = null;
                    ApplyLayout();
                }
                Pump();
            }
            else
            {
                c.Remaining = Math.Max(0, 1 - t);
                if (_frames % 10 == 0) c.UpdateAge();
            }
        }
        if (_valueUntil is { } vu && now >= vu)
        {
            _vm.ShowValue = false;
            _valueUntil = null;
        }
        if (_toastUntil is { } tu && now >= tu)
        {
            _vm.Toast = "";
            _toastUntil = null;
        }
        Refresh();
    }

    private void Refresh()
    {
        var active = _vm.Card is not null || _vm.ShowValue || _vm.Toast.Length > 0;
        if (active)
        {
            if (_window is null)
            {
                _window = new OverlayWindow { DataContext = _vm };
                _window.ApplyLayout(Layout);
            }
            if (!_window.IsVisible)
            {
                _window.Show();
                _window.ApplyLayout(Layout);
            }
            if (!_frame.IsEnabled) _frame.Start();
        }
        else
        {
            if (_window is { IsVisible: true }) _window.Hide();
            _frame.Stop();
        }
    }

    private void ApplyLayout()
    {
        _vm.Scale = Layout.SizePercent / 100.0;
        _window?.ApplyLayout(Layout);
    }

    public void Dispose()
    {
        _frame.Stop();
        _window?.Close();
        _window = null;
    }
}
