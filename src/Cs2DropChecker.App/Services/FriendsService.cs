using Cs2DropChecker.Core.Friends;
using Cs2DropChecker.Core.Profit;
using Cs2DropChecker.Core.Steam;
using Cs2DropChecker.Core.Valuation;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Services;

/// <summary>
/// Checks friends' public inventories on a slow timer. Requests are spaced out and a round stops as soon as Steam
/// rate-limits, so the user's own inventory checks keep priority.
/// </summary>
public sealed class FriendsService(AppServices s, ILogger log) : IDisposable
{
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private CancellationTokenSource? _cts;
    private TimeSpan _backoff = TimeSpan.Zero;

    public bool Busy { get; private set; }
    public DateTimeOffset? LastRound { get; private set; }
    public DateTimeOffset? NextRound { get; private set; }
    public string Problem { get; private set; } = "";

    /// <summary>Raised on the UI thread when friend data or the refresh state changes.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void RefreshNow() => _wake.Release();

    private void Raise() => s.Ui.BeginInvoke(() => Changed?.Invoke(this, EventArgs.Empty));

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            NextRound = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
            await WaitAsync(TimeSpan.FromMinutes(2), ct);
            while (!ct.IsCancellationRequested)
            {
                await RoundAsync(ct);
                var wait = TimeSpan.FromMinutes(s.Settings.Friends.RefreshMinutes) + _backoff;
                NextRound = DateTimeOffset.UtcNow + wait;
                Raise();
                await WaitAsync(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Friends loop crashed");
        }
    }

    private async Task WaitAsync(TimeSpan wait, CancellationToken ct)
    {
        try { await _wake.WaitAsync(wait, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        while (_wake.CurrentCount > 0) _wake.Wait(0, CancellationToken.None);
    }

    private async Task RoundAsync(CancellationToken ct)
    {
        List<Friend> friends;
        try { friends = s.Db.GetFriends().Where(f => f.Tracked).OrderBy(f => f.CheckedAt ?? DateTimeOffset.MinValue).ToList(); }
        catch (Exception ex)
        {
            log.LogWarning("Could not load friends: {Message}", ex.Message);
            return;
        }
        if (friends.Count == 0) return;
        Busy = true;
        Problem = "";
        Raise();
        try
        {
            var first = true;
            foreach (var f in friends)
            {
                if (!first) await Task.Delay(Spacing, ct);
                first = false;
                if (!await CheckAsync(f, ct))
                {
                    _backoff = _backoff == TimeSpan.Zero ? TimeSpan.FromMinutes(15) : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, TimeSpan.FromHours(2).Ticks));
                    Problem = "Steam is rate-limiting requests · the rest wait for the next round";
                    log.LogInformation("Friends round stopped by Steam's rate limit");
                    return;
                }
                Raise();
            }
            _backoff = TimeSpan.Zero;
        }
        finally
        {
            Busy = false;
            LastRound = DateTimeOffset.UtcNow;
            Raise();
        }
    }

    /// <summary>Returns false when Steam rate-limited the request.</summary>
    private async Task<bool> CheckAsync(Friend f, CancellationToken ct)
    {
        InventoryFetchResult r;
        try { r = await s.Fetcher.FetchAsync(s.HttpTransport, f.SteamId, [InventoryFetcher.MainContext], ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { r = new InventoryFetchResult(FetchStatus.Failed, null, ex.Message); }
        var now = DateTimeOffset.UtcNow;
        try
        {
            switch (r.Status)
            {
                case FetchStatus.Ok:
                    var snap = r.Snapshot!;
                    var cur = s.Settings.Currency;
                    var prev = s.Db.LoadFriendSnapshot(f.SteamId);
                    var key = s.KeyPriceIn(cur);
                    foreach (var a in FriendTracker.Arrivals(f.SteamId, prev, snap, s.Prices.GetCached, key, cur))
                        s.Db.AddFriendArrival(a);
                    s.Db.SaveFriendSnapshot(snap);
                    var value = InventoryValuation.Compute(snap.Items, i => s.Prices.GetCached(i.MarketHashName)?.Price);
                    s.Db.UpdateFriendStatus(f.SteamId, FriendStatus.Ok, now, value.Total, cur, snap.Items.Count);
                    return true;
                case FetchStatus.Private:
                    s.Db.UpdateFriendStatus(f.SteamId, FriendStatus.Private, now, null, null, null);
                    return true;
                case FetchStatus.NotFound:
                    s.Db.UpdateFriendStatus(f.SteamId, FriendStatus.NotFound, now, null, null, null);
                    return true;
                case FetchStatus.RateLimited:
                    return false;
                default:
                    s.Db.UpdateFriendStatus(f.SteamId, FriendStatus.Failed, now, f.Value, f.Currency, f.ItemCount);
                    return true;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not save a friend's inventory: {Message}", ex.Message);
            return true;
        }
    }

    /// <summary>Adds any public profile by link, SteamID64 or custom URL name.</summary>
    public async Task<Friend> AddManualAsync(string input, CancellationToken ct)
    {
        var profile = await s.Profiles.ResolveAsync(input, ct);
        if (profile.SteamId == s.Settings.SteamId64) throw new SteamLookupException("That's your own profile. You're always on the leaderboard.");
        var friend = new Friend
        {
            SteamId = profile.SteamId,
            Name = profile.Name,
            AvatarUrl = profile.AvatarUrl,
            Source = FriendSource.Manual,
            Tracked = true,
            AddedAt = DateTimeOffset.UtcNow,
        };
        s.Db.UpsertFriend(friend);
        Raise();
        RefreshNow();
        return friend;
    }

    /// <summary>Reads the signed-in user's friends list through the built-in browser. Needs Steam sign-in.</summary>
    public async Task<IReadOnlyList<SteamFriend>> LoadSteamFriendsAsync(CancellationToken ct)
    {
        if (!s.Session.IsSignedIn) throw new InvalidOperationException("Sign in to Steam to use this.");
        var r = await s.Session.FetchAsync("/my/friends/", ct);
        if (r.Status is < 200 or >= 300) throw new InvalidOperationException(r.Status < 0 ? "Steam could not be reached." : $"Steam answered {r.Status}.");
        if (Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
        {
            s.Session.MarkSignedOut();
            throw new InvalidOperationException("Your Steam sign-in expired. Sign in again to load your friends.");
        }
        return FriendsListParser.Parse(r.Body).Where(f => f.SteamId != s.Settings.SteamId64).ToList();
    }

    /// <summary>Saves which Steam friends to follow. Unticked Steam friends stay in the list but are not checked.</summary>
    public void SaveSteamSelection(IEnumerable<(SteamFriend Friend, bool Tracked)> selection)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = s.Db.GetFriends().ToDictionary(f => f.SteamId);
        foreach (var (f, tracked) in selection)
        {
            if (!tracked && !existing.ContainsKey(f.SteamId)) continue;
            if (existing.TryGetValue(f.SteamId, out var e) && e.Source == FriendSource.Manual && !tracked) continue;
            s.Db.UpsertFriend(new Friend
            {
                SteamId = f.SteamId,
                Name = f.Name,
                AvatarUrl = f.AvatarUrl,
                Source = FriendSource.Steam,
                Tracked = tracked,
                AddedAt = existing.TryGetValue(f.SteamId, out var old) ? old.AddedAt : now,
            });
        }
        Raise();
        RefreshNow();
    }

    public void SetTracked(ulong id, bool tracked)
    {
        s.Db.SetFriendTracked(id, tracked);
        Raise();
    }

    public void Remove(ulong id)
    {
        s.Db.RemoveFriend(id);
        Raise();
    }

    public void Dispose() => _cts?.Cancel();
}
