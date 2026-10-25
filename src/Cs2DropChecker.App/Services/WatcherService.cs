using Cs2DropChecker.Core.Detection;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Settings;
using Cs2DropChecker.Core.Steam;
using Cs2DropChecker.Core.Valuation;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Services;

public enum WatchState { NotSetUp, Starting, Checking, Watching, WaitingForCs2, RateLimited, Private, NotFound, Offline }

public sealed record DropsAddedEventArgs(IReadOnlyList<DropRecord> Drops, bool ShowCards);

public sealed record DropPricedEventArgs(long DropId, PriceQuote? Quote);

/// <summary>
/// Polls the watched inventory, records what arrived and why, and keeps the inventory value current.
/// All events are raised on the UI thread.
/// </summary>
public sealed class WatcherService : IDisposable
{
    private readonly AppServices _s;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _priceWake = new(0, int.MaxValue);
    private readonly TaskCompletionSource _firstPrices = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private volatile bool _force;
    private volatile bool _closePoll;
    private DateTimeOffset _cs2ClosedAt;
    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset _nextDue;
    private TimeSpan _backoff = TimeSpan.Zero;
    private (WatchState State, string Message)? _problem;
    private int _suspicious;
    private bool _retrySoon;

    public WatcherService(AppServices services, ILogger log)
    {
        _s = services;
        _log = log;
    }

    public WatchState State { get; private set; } = WatchState.Starting;
    public string Problem => _problem?.Message ?? "";
    public DateTimeOffset? NextPollAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public InventorySnapshot? Current { get; private set; }
    public InventoryValue Value { get; private set; } = InventoryValue.Empty;
    public string? ValueSourceId { get; private set; }
    public bool LastPollSignedIn { get; private set; }
    public FetchStatus? LastStatus { get; private set; }

    public event EventHandler? StatusChanged;
    public event EventHandler? InventoryChanged;
    public event EventHandler<DropsAddedEventArgs>? DropsAdded;
    public event EventHandler<DropPricedEventArgs>? DropPriced;
    public event EventHandler? SessionExpired;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _s.Cs2.RunningChanged += OnCs2Changed;
        _s.Prices.PricesUpdated += (_, _) =>
        {
            RecomputeValue(recordPoint: false, force: false);
            Ui(() => InventoryChanged?.Invoke(this, EventArgs.Empty));
        };
        _ = Task.Run(() => PriceLoopAsync(ct));
        _ = Task.Run(() => LoopAsync(ct));
    }

    public void PollNow()
    {
        _force = true;
        _wake.Release();
    }

    public void RefreshPricesNow() => _priceWake.Release();

    /// <summary>Call after the watched account or currency changes.</summary>
    public void Reset(bool accountChanged)
    {
        if (accountChanged)
        {
            Current = null;
            Value = InventoryValue.Empty;
            LastSuccessAt = null;
            _problem = null;
            _backoff = TimeSpan.Zero;
            Ui(() => InventoryChanged?.Invoke(this, EventArgs.Empty));
            PollNow();
        }
        else
        {
            RecomputeValue(recordPoint: false, force: false);
            Ui(() => InventoryChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    private void Ui(Action a) => _s.Ui.BeginInvoke(a);

    private void OnCs2Changed(object? sender, bool running)
    {
        if (running)
        {
            if (Current is null || DateTimeOffset.UtcNow - Current.TakenAt > TimeSpan.FromMinutes(5)) _force = true;
        }
        else
        {
            _closePoll = true;
            _cs2ClosedAt = DateTimeOffset.UtcNow;
        }
        _wake.Release();
    }

    private async Task PriceLoopAsync(CancellationToken ct)
    {
        _s.Prices.Live.Warm(_s.Settings.Currency);
        while (!ct.IsCancellationRequested)
        {
            try { await _s.Prices.RefreshBulkAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogWarning("Price refresh failed: {Message}", ex.Message); }
            _firstPrices.TrySetResult();
            try { await _priceWake.WaitAsync(TimeSpan.FromMinutes(10), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            LoadCachedSnapshot();
            while (!ct.IsCancellationRequested)
            {
                var s = _s.Settings;
                var now = DateTimeOffset.UtcNow;
                if (s.SteamId64 == 0 || !s.FirstRunDone)
                {
                    SetState(WatchState.NotSetUp, null);
                    await WaitAsync(TimeSpan.FromMinutes(5), ct);
                    continue;
                }

                var interval = TimeSpan.FromMinutes(s.PollIntervalMinutes);
                var cs2 = _s.Cs2.IsRunning;
                var closeDue = _closePoll && now >= _cs2ClosedAt + TimeSpan.FromSeconds(20);
                var due = _force || _lastAttempt is null || closeDue
                    || ((!s.WatchOnlyWhileCs2Running || cs2) && now >= _nextDue);
                if (due)
                {
                    _force = false;
                    if (closeDue) _closePoll = false;
                    _retrySoon = false;
                    await PollOnceAsync(ct);
                    _lastAttempt = DateTimeOffset.UtcNow;
                    _nextDue = _lastAttempt.Value + (_retrySoon ? TimeSpan.FromSeconds(45) : interval + _backoff);
                    continue;
                }

                var waitingForGame = s.WatchOnlyWhileCs2Running && !cs2 && !_closePoll;
                if (_problem is { } p) SetState(p.State, waitingForGame ? null : _nextDue);
                else if (waitingForGame) SetState(WatchState.WaitingForCs2, null);
                else SetState(WatchState.Watching, _closePoll ? _cs2ClosedAt + TimeSpan.FromSeconds(20) : _nextDue);

                var target = _closePoll ? _cs2ClosedAt + TimeSpan.FromSeconds(20) : waitingForGame ? now + TimeSpan.FromMinutes(5) : _nextDue;
                var wait = target - now;
                if (wait < TimeSpan.FromMilliseconds(250)) wait = TimeSpan.FromMilliseconds(250);
                if (wait > TimeSpan.FromMinutes(5)) wait = TimeSpan.FromMinutes(5);
                await WaitAsync(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Watcher loop crashed");
        }
    }

    private async Task WaitAsync(TimeSpan wait, CancellationToken ct)
    {
        try { await _wake.WaitAsync(wait, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        while (_wake.CurrentCount > 0) _wake.Wait(0, CancellationToken.None);
    }

    private void SetState(WatchState state, DateTimeOffset? next)
    {
        if (State == state && NextPollAt == next) return;
        State = state;
        NextPollAt = next;
        Ui(() => StatusChanged?.Invoke(this, EventArgs.Empty));
    }

    private void LoadCachedSnapshot()
    {
        var id = _s.Settings.SteamId64;
        if (id == 0) return;
        try
        {
            var snap = _s.Db.LoadLatestSnapshot(id);
            if (snap is null) return;
            Current = snap;
            LastSuccessAt = snap.TakenAt;
            LastPollSignedIn = snap.SignedIn;
            RecomputeValue(recordPoint: false, force: false);
            Ui(() => InventoryChanged?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not load the saved inventory: {Message}", ex.Message);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var s = _s.Settings;
        var id = s.SteamId64;
        SetState(WatchState.Checking, null);

        var session = _s.Session;
        var signed = s.UseSignIn && session.IsSignedIn && session.SignedInSteamId == id;
        InventoryFetchResult r;
        try
        {
            r = signed
                ? await _s.Fetcher.FetchAsync(_s.WebTransport, id, [InventoryFetcher.MainContext, InventoryFetcher.ProtectedContext], ct)
                : await _s.Fetcher.FetchAsync(_s.HttpTransport, id, [InventoryFetcher.MainContext], ct);

            if (signed && r.Status == FetchStatus.SignedOut)
            {
                _log.LogInformation("Steam sign-in expired; falling back to the public inventory");
                session.MarkSignedOut();
                Ui(() => SessionExpired?.Invoke(this, EventArgs.Empty));
                signed = false;
                r = await _s.Fetcher.FetchAsync(_s.HttpTransport, id, [InventoryFetcher.MainContext], ct);
            }
            else if (signed && r.Status == FetchStatus.Failed)
            {
                _log.LogInformation("Signed-in fetch failed ({Message}); trying the public inventory", r.Message);
                signed = false;
                r = await _s.Fetcher.FetchAsync(_s.HttpTransport, id, [InventoryFetcher.MainContext], ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            r = new InventoryFetchResult(FetchStatus.Failed, null, ex.Message);
        }

        LastStatus = r.Status;
        try { _s.Db.AddPollLog(signed ? "signed-in" : "public", r.Status.ToString(), r.Snapshot?.Items.Count ?? 0, r.Message); }
        catch (Exception) { }
        _log.LogInformation("Poll {Mode}: {Status} {Message}", signed ? "signed-in" : "public", r.Status, r.Message);

        switch (r.Status)
        {
            case FetchStatus.Ok:
                _backoff = TimeSpan.Zero;
                _problem = null;
                await HandleSnapshotAsync(r.Snapshot!, ct);
                break;
            case FetchStatus.RateLimited:
                _backoff = _backoff == TimeSpan.Zero ? TimeSpan.FromMinutes(5) : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, TimeSpan.FromMinutes(30).Ticks));
                _problem = (WatchState.RateLimited, "Steam is rate-limiting requests");
                break;
            case FetchStatus.Private:
                _backoff = TimeSpan.FromMinutes(7);
                _problem = (WatchState.Private, "Inventory is private");
                break;
            case FetchStatus.NotFound:
                _backoff = TimeSpan.FromMinutes(10);
                _problem = (WatchState.NotFound, "No CS2 inventory on this profile");
                break;
            default:
                _backoff = _backoff == TimeSpan.Zero ? TimeSpan.FromMinutes(1) : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, TimeSpan.FromMinutes(15).Ticks));
                _problem = (WatchState.Offline, r.Message);
                break;
        }
        Ui(() => StatusChanged?.Invoke(this, EventArgs.Empty));
    }

    private static bool LooksIncomplete(InventorySnapshot prev, InventorySnapshot next)
    {
        var before = prev.VisibleMainCount;
        var after = next.VisibleMainCount;
        return before >= 20 && after < before * 0.6;
    }

    private async Task HandleSnapshotAsync(InventorySnapshot snap, CancellationToken ct)
    {
        var s = _s.Settings;
        var prev = Current;
        if (prev is not null && prev.SteamId != snap.SteamId) prev = null;

        if (prev is not null && LooksIncomplete(prev, snap) && _suspicious++ < 1)
        {
            _log.LogWarning("Inventory shrank from {Before} to {After} items; checking again before trusting it", prev.VisibleMainCount, snap.VisibleMainCount);
            _retrySoon = true;
            return;
        }
        _suspicious = 0;

        var added = new List<DropRecord>();
        var showCards = false;
        if (prev is not null)
        {
            var cs2Active = _s.Cs2.IsRunning || (_s.Cs2.LastSeenRunning is { } seen && seen >= prev.TakenAt);
            var diff = DropClassifier.Classify(prev, snap, cs2Active);
            var gap = snap.TakenAt - prev.TakenAt;
            showCards = gap <= TimeSpan.FromMinutes(Math.Max(30, s.PollIntervalMinutes * 3));
            var recordable = diff.Added.Where(a => ArrivalKindText.ShowsCard(a.Kind)).ToList();
            if (recordable.Count > 40) showCards = false;
            foreach (var a in recordable)
            {
                var rec = new DropRecord
                {
                    SteamId = snap.SteamId,
                    SeenAt = snap.TakenAt,
                    Kind = a.Kind,
                    SeenLate = a.SeenLate,
                    Item = a.Item,
                };
                try
                {
                    var rowId = _s.Db.AddDrop(rec);
                    if (rowId > 0) added.Add(rec with { Id = rowId });
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Could not save a new item: {Message}", ex.Message);
                }
            }
            if (diff.Added.Count > 0)
                _log.LogInformation("Diff: {Added} added ({Recorded} recorded), {Removed} removed, cards {Show}",
                    diff.Added.Count, added.Count, diff.Removed.Count, showCards);
        }

        try { _s.Db.SaveSnapshot(snap); }
        catch (Exception ex) { _log.LogWarning("Could not save the inventory snapshot: {Message}", ex.Message); }
        Current = snap;
        LastSuccessAt = snap.TakenAt;
        LastPollSignedIn = snap.SignedIn;

        await Task.WhenAny(_firstPrices.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));
        RecomputeValue(recordPoint: true, force: added.Count > 0);
        Ui(() => InventoryChanged?.Invoke(this, EventArgs.Empty));

        if (added.Count > 0)
        {
            Ui(() => DropsAdded?.Invoke(this, new DropsAddedEventArgs(added, showCards)));
            _ = Task.Run(() => PriceDropsAsync(added, ct), ct);
        }
    }

    private async Task PriceDropsAsync(IReadOnlyList<DropRecord> drops, CancellationToken ct)
    {
        foreach (var d in drops)
        {
            PriceQuote? q = null;
            try { q = await _s.Prices.GetForDropAsync(d.Item.MarketHashName, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogInformation("Price for a new item failed: {Message}", ex.Message); }
            if (q is not null)
            {
                try { _s.Db.UpdateDropPrice(d.Id, q.Price, q.Currency, q.SourceId); }
                catch (Exception) { }
            }
            var quote = q;
            Ui(() => DropPriced?.Invoke(this, new DropPricedEventArgs(d.Id, quote)));
        }
        RecomputeValue(recordPoint: false, force: false);
        Ui(() => InventoryChanged?.Invoke(this, EventArgs.Empty));
    }

    public void RecomputeValue(bool recordPoint, bool force)
    {
        var snap = Current;
        if (snap is null)
        {
            Value = InventoryValue.Empty;
            return;
        }
        var counts = new Dictionary<string, int>();
        var value = InventoryValuation.Compute(snap.Items, item =>
        {
            var q = _s.Prices.GetCached(item.MarketHashName);
            if (q is null) return null;
            counts[q.SourceId] = counts.GetValueOrDefault(q.SourceId) + 1;
            return q.Price;
        });
        Value = value;
        ValueSourceId = counts.Count == 0 ? null : counts.MaxBy(kv => kv.Value).Key;
        if (!recordPoint) return;

        if (value.ItemCount == 0 || value.PricedCount < value.ItemCount * 0.4) return;
        try
        {
            var currency = _s.Settings.Currency;
            var last = _s.Db.GetLastValuePoint(snap.SteamId, currency);
            var now = DateTimeOffset.UtcNow;
            var add = last is null || force
                || now - last.At >= TimeSpan.FromHours(3)
                || (now - last.At >= TimeSpan.FromMinutes(15) && Math.Abs(value.Total - last.Total) >= Math.Max(0.5m, last.Total * 0.0025m));
            if (add) _s.Db.AddValuePoint(snap.SteamId, now, value.Total, currency, value.PricedCount, value.ItemCount);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not save the value history: {Message}", ex.Message);
        }
    }

    /// <summary>Change since the last value point before local midnight, and the number of new items today.</summary>
    public (decimal? Delta, int NewToday) Today()
    {
        var snap = Current;
        if (snap is null) return (null, 0);
        var midnight = new DateTimeOffset(DateTime.Today);
        try
        {
            var before = _s.Db.GetLastValuePoint(snap.SteamId, _s.Settings.Currency, midnight);
            var newToday = _s.Db.CountDrops(snap.SteamId, midnight);
            return (before is null ? null : Value.Total - before.Total, newToday);
        }
        catch (Exception)
        {
            return (null, 0);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _s.Cs2.RunningChanged -= OnCs2Changed;
    }
}
