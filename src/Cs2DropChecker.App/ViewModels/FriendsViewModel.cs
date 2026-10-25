using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Friends;
using Cs2DropChecker.Core.Steam;
using Cs2DropChecker.Core.Weekly;

namespace Cs2DropChecker.App.ViewModels;

public sealed record LeaderRow(
    string Rank, string Name, string AvatarUrl, bool IsYou, bool IsPrivate, string Status,
    string Best, string BestHex, string BestValue, string Week, string WeekSub, string Inventory,
    string CaseNet, string CaseTrend, string CaseSub);

public sealed partial class FriendRow : ObservableObject
{
    private readonly Action<FriendRow, bool> _onTracked;
    private bool _init = true;

    public FriendRow(Friend f, Action<FriendRow, bool> onTracked)
    {
        Friend = f;
        _onTracked = onTracked;
        Name = f.Name;
        AvatarUrl = f.AvatarUrl;
        SourceText = f.Source == FriendSource.Steam ? "Steam friend" : "Added by hand";
        StatusText = FriendStatus.Label(f.Status) + (f.CheckedAt is { } at ? " · checked " + When.Ago(at) : "");
        StatusKind = f.Status == FriendStatus.Ok ? "ok" : f.Status == FriendStatus.Pending ? "" : "warn";
        Tracked = f.Tracked;
        _init = false;
    }

    public Friend Friend { get; }
    public string Name { get; }
    public string AvatarUrl { get; }
    public string SourceText { get; }
    public string StatusText { get; }
    public string StatusKind { get; }
    [ObservableProperty] private bool _tracked;

    partial void OnTrackedChanged(bool value)
    {
        if (!_init) _onTracked(this, value);
    }
}

public sealed partial class SteamFriendRow(SteamFriend friend, bool selected) : ObservableObject
{
    public SteamFriend Friend { get; } = friend;
    public string Name => Friend.Name;
    public string AvatarUrl => Friend.AvatarUrl;
    [ObservableProperty] private bool _selected = selected;
}

public sealed partial class FriendsViewModel : ObservableObject
{
    private readonly AppServices _s;

    public FriendsViewModel(AppServices s)
    {
        _s = s;
        s.Friends.Changed += (_, _) => Reload();
        s.Session.Changed += (_, _) => UpdateSession();
        UpdateSession();
    }

    public ObservableCollection<LeaderRow> LeaderRows { get; } = new();
    public ObservableCollection<FriendRow> Friends { get; } = new();
    public ObservableCollection<SteamFriendRow> SteamFriends { get; } = new();

    [ObservableProperty] private string _addInput = "";
    [ObservableProperty] private bool _addBusy;
    [ObservableProperty] private string _addMessage = "";
    [ObservableProperty] private string _addKind = "";
    [ObservableProperty] private bool _signedIn;
    [ObservableProperty] private bool _steamBusy;
    [ObservableProperty] private string _steamMessage = "";
    [ObservableProperty] private string _steamKind = "";
    [ObservableProperty] private string _refreshText = "";
    [ObservableProperty] private string _weekText = "";
    [ObservableProperty] private bool _hasFriends;

    public void UpdateSession()
    {
        SignedIn = _s.Session.IsSignedIn;
        if (!SignedIn) SteamFriends.Clear();
    }

    public void Reload()
    {
        var cur = _s.Settings.Currency;
        var now = DateTimeOffset.UtcNow;
        var start = Cs2Week.Start(now);
        WeekText = "This CS2 week · since " + start.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture)
            + " · resets " + Cs2Week.NextReset(now).ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture);

        List<Friend> friends;
        try { friends = _s.Db.GetFriends(); }
        catch (Exception) { friends = new(); }

        Friends.Clear();
        foreach (var f in friends) Friends.Add(new FriendRow(f, OnTracked));
        HasFriends = Friends.Count > 0;

        var rows = new List<LeaderboardRow>();
        var settings = _s.Settings;
        var id = settings.SteamId64;
        if (id != 0)
        {
            try
            {
                var drops = _s.Db.GetDrops(id, 3000).Where(d => d.SeenAt >= start).ToList();
                var w = _s.Watcher;
                decimal? value = w.Current is null ? null : w.Value.Total;
                var profit = ProfitFigures.Load(_s).Lifetime;
                var name = settings.ProfileName.Length > 0 ? settings.ProfileName : "You";
                rows.Add(Core.Friends.Leaderboard.ForYou(id, name, settings.AvatarUrl, drops, value, profit, cur, now));
            }
            catch (Exception) { }
        }
        foreach (var f in friends.Where(f => f.Tracked))
        {
            try { rows.Add(Core.Friends.Leaderboard.ForFriend(f, _s.Db.GetFriendArrivals(f.SteamId), cur, now)); }
            catch (Exception) { }
        }

        LeaderRows.Clear();
        var rank = 0;
        foreach (var r in Core.Friends.Leaderboard.Rank(rows))
        {
            var pending = r.Status == FriendStatus.Pending;
            var unusable = r.IsPrivate || r.Status is FriendStatus.NotFound;
            LeaderRows.Add(new LeaderRow(
                unusable || pending ? "—" : (++rank).ToString(CultureInfo.InvariantCulture),
                r.Name,
                r.AvatarUrl,
                r.IsYou,
                unusable,
                r.IsYou ? "You" : FriendStatus.Label(r.Status),
                r.BestName ?? (unusable ? "" : "—"),
                r.BestRarityHex ?? "6f7f95",
                r.BestValue is { } bv ? Money.Format(bv, cur) : "",
                unusable ? "" : Money.Format(r.WeekValue, cur),
                unusable ? "" : $"{r.WeekCount} item{(r.WeekCount == 1 ? "" : "s")}",
                r.InventoryValue is { } iv && !unusable ? Money.Format(iv, cur) : "—",
                r.CaseNet is { } cn ? Core.Profit.ProfitCalculator.Signed(cn, cur) : "—",
                ProfitFigures.Trend(r.CaseNet),
                r.CaseNet is null ? "" : r.CaseNetEstimated ? $"estimate · {r.CaseCount} seen" : $"{r.CaseCount} opening{(r.CaseCount == 1 ? "" : "s")}"));
        }

        UpdateRefreshText();
    }

    public void UpdateRefreshText()
    {
        var fs = _s.Friends;
        RefreshText = fs.Busy ? "Checking friends' inventories…"
            : fs.Problem.Length > 0 ? fs.Problem
            : (fs.LastRound is { } lr ? "Last checked " + When.Ago(lr) : "Not checked yet")
              + (fs.NextRound is { } nr ? " · next in " + When.Short(nr - DateTimeOffset.UtcNow) : "")
              + $" · every {_s.Settings.Friends.RefreshMinutes} min";
    }

    private void OnTracked(FriendRow row, bool tracked)
    {
        try { _s.Friends.SetTracked(row.Friend.SteamId, tracked); }
        catch (Exception) { }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var input = AddInput.Trim();
        if (input.Length == 0) return;
        AddBusy = true;
        AddMessage = "Checking with Steam…";
        AddKind = "";
        try
        {
            var f = await _s.Friends.AddManualAsync(input, CancellationToken.None);
            AddInput = "";
            AddMessage = $"Added {f.Name}. Their inventory is checked in the background; new items count from the next check on.";
            AddKind = "ok";
        }
        catch (SteamLookupException ex)
        {
            AddMessage = ex.Message;
            AddKind = "warn";
        }
        catch (Exception ex)
        {
            AddMessage = "Could not reach Steam: " + ex.Message;
            AddKind = "warn";
        }
        finally
        {
            AddBusy = false;
        }
    }

    [RelayCommand]
    private void Remove(FriendRow row)
    {
        try { _s.Friends.Remove(row.Friend.SteamId); }
        catch (Exception) { }
    }

    [RelayCommand]
    private void RefreshNow()
    {
        _s.Friends.RefreshNow();
        RefreshText = "Checking friends' inventories…";
    }

    [RelayCommand]
    private async Task LoadSteamAsync()
    {
        if (!_s.Session.IsSignedIn) return;
        SteamBusy = true;
        SteamMessage = "Loading your friends list…";
        SteamKind = "";
        try
        {
            var list = await _s.Friends.LoadSteamFriendsAsync(CancellationToken.None);
            var tracked = _s.Db.GetFriends().Where(f => f.Tracked).Select(f => f.SteamId).ToHashSet();
            SteamFriends.Clear();
            foreach (var f in list.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)) SteamFriends.Add(new SteamFriendRow(f, tracked.Contains(f.SteamId)));
            SteamMessage = list.Count == 0 ? "No friends found on your Steam profile." : $"{list.Count} friends · tick the ones to compare with, then save.";
            SteamKind = list.Count == 0 ? "warn" : "";
        }
        catch (Exception ex)
        {
            SteamMessage = ex.Message;
            SteamKind = "warn";
        }
        finally
        {
            SteamBusy = false;
            UpdateSession();
        }
    }

    [RelayCommand]
    private void SaveSteam()
    {
        if (SteamFriends.Count == 0) return;
        try
        {
            _s.Friends.SaveSteamSelection(SteamFriends.Select(r => (r.Friend, r.Selected)));
            var n = SteamFriends.Count(r => r.Selected);
            SteamMessage = $"Saved · following {n} Steam friend{(n == 1 ? "" : "s")}.";
            SteamKind = "ok";
        }
        catch (Exception ex)
        {
            SteamMessage = "Could not save: " + ex.Message;
            SteamKind = "warn";
        }
    }

    [RelayCommand]
    private void SelectAll(string on)
    {
        var value = on == "true";
        foreach (var r in SteamFriends) r.Selected = value;
    }
}
