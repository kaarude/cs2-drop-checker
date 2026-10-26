using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Controls;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Storage;
using Cs2DropChecker.Core.Weekly;

namespace Cs2DropChecker.App.ViewModels;

public sealed partial class OverviewViewModel(AppServices s) : ObservableObject
{
    private List<DropRecord> _drops = new();

    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private string _valueSub = "Waiting for the first check";
    [ObservableProperty] private string _valueChange = "";
    [ObservableProperty] private string _valueChangeTrend = "";
    [ObservableProperty] private string _weeklyStatus = "";
    [ObservableProperty] private string _weeklyStatusKind = "";
    [ObservableProperty] private string _weeklyStatusTip = "";
    [ObservableProperty] private string _newCount = "0";
    [ObservableProperty] private string _newSub = "";
    [ObservableProperty] private string _bestName = "—";
    [ObservableProperty] private string _bestSub = "No drops yet";
    [ObservableProperty] private bool _bestStar;
    [ObservableProperty] private string _weeklyLabel = "Weekly drops · " + DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _weeklyCount = "0";
    [ObservableProperty] private string _weeklySub = "";
    [ObservableProperty] private IReadOnlyList<ValuePoint> _points = [];
    [ObservableProperty] private IReadOnlyList<ChartMarker> _markers = [];
    [ObservableProperty] private DateTimeOffset? _from;
    [ObservableProperty] private string _currency = "EUR";
    [ObservableProperty] private string _range = "30D";
    [ObservableProperty] private int _totalDrops;
    [ObservableProperty] private string _viewAllText = "View all";
    [ObservableProperty] private string _profitHeadline = "No priced openings yet";
    [ObservableProperty] private string _profitTrend = "";
    [ObservableProperty] private string _profitSub = "";
    [ObservableProperty] private string _profitWeek = "—";
    [ObservableProperty] private string _profitWeekTrend = "";
    [ObservableProperty] private string _profitMonth = "—";
    [ObservableProperty] private string _profitMonthTrend = "";
    [ObservableProperty] private string _profitRoi = "—";
    [ObservableProperty] private string _profitRoiTrend = "";
    [ObservableProperty] private bool _hasOpenings;

    public void ReloadProfit()
    {
        var f = ProfitFigures.Load(s);
        var life = f.Lifetime;
        HasOpenings = f.Openings.Count > 0;
        ProfitHeadline = f.Headline;
        ProfitTrend = life.Counted == 0 ? "" : ProfitFigures.Trend(life.Net);
        ProfitSub = f.Openings.Count == 0 ? "Case and capsule openings are costed with the case and key price at the time"
            : $"Lifetime · {life.Counted} of {life.Openings} openings priced";
        ProfitWeek = f.Signed(f.Week);
        ProfitWeekTrend = f.Week.Counted == 0 ? "" : ProfitFigures.Trend(f.Week.Net);
        ProfitMonth = f.Signed(f.Month);
        ProfitMonthTrend = f.Month.Counted == 0 ? "" : ProfitFigures.Trend(f.Month.Net);
        ProfitRoi = f.Roi;
        ProfitRoiTrend = life.RoiPercent is { } r ? ProfitFigures.Trend(r) : "";
    }

    public ObservableCollection<DropRowViewModel> Recent { get; } = new();

    partial void OnRangeChanged(string value) => ReloadChart();

    public void Reload()
    {
        var id = s.Settings.SteamId64;
        Currency = s.Settings.Currency;
        Recent.Clear();
        if (id == 0)
        {
            _drops = new();
            TotalDrops = 0;
            ViewAllText = "View all";
            NewCount = WeeklyCount = "0";
            NewSub = WeeklySub = "";
            BestName = "—";
            BestSub = "No drops yet";
            BestStar = false;
            WeeklyStatus = WeeklyStatusTip = WeeklyStatusKind = "";
            RefreshValue();
            ReloadChart();
            ReloadProfit();
            return;
        }
        try { _drops = s.Db.GetDrops(id, 50000); }
        catch (Exception) { _drops = new(); }

        var cur = Currency;
        TotalDrops = _drops.Count;
        ViewAllText = TotalDrops > 0 ? $"View all {TotalDrops}" : "View all";

        var since30 = DateTimeOffset.UtcNow.AddDays(-30);
        var last30 = _drops.Where(d => d.SeenAt >= since30).ToList();
        NewCount = last30.Count.ToString(CultureInfo.InvariantCulture);
        NewSub = last30.Count == 0 ? "Nothing new yet" : Money.Format(SumAtDrop(last30, cur), cur) + " at drop time";

        var best = _drops.Where(d => d.PriceAtDrop.HasValue && d.Currency == cur).MaxBy(d => d.PriceAtDrop!.Value);
        if (best is not null)
        {
            BestName = best.Item.Name;
            BestStar = best.Item.IsStar;
            BestSub = $"{Money.Format(best.PriceAtDrop!.Value, cur)} · {ArrivalKindText.Label(best.Kind)} · {When.Format(best.SeenAt)}";
        }
        else
        {
            BestName = "—";
            BestStar = false;
            BestSub = _drops.Count == 0 ? "No drops yet" : "Prices still loading";
        }

        var year = DateTime.Today.Year;
        WeeklyLabel = "Weekly drops · " + year.ToString(CultureInfo.InvariantCulture);
        var weekly = _drops.Where(d => d.Kind == ArrivalKind.WeeklyDrop && d.SeenAt.ToLocalTime().Year == year).ToList();
        WeeklyCount = weekly.Count.ToString(CultureInfo.InvariantCulture);
        WeeklySub = weekly.Count == 0 ? "Play CS2 to earn them" : Money.Format(SumAtDrop(weekly, cur), cur) + " total value";
        UpdateWeeklyStatus();

        foreach (var d in _drops.Take(8)) Recent.Add(new DropRowViewModel(d, s.Prices.GetCached(d.Item.MarketHashName)));
        RefreshValue();
        ReloadChart();
        ReloadProfit();
    }

    private static decimal SumAtDrop(IEnumerable<DropRecord> drops, string currency) =>
        drops.Where(d => d.PriceAtDrop.HasValue && d.Currency == currency).Sum(d => d.PriceAtDrop!.Value);

    public void RefreshValue()
    {
        var w = s.Watcher;
        var v = w.Value;
        if (w.Current is null || v.ItemCount == 0)
        {
            ValueText = "—";
            ValueSub = w.Current is null ? "Waiting for the first check" : "Inventory is empty";
        }
        else
        {
            ValueText = Money.Format(v.Total, s.Settings.Currency);
            ValueSub = $"{v.PricedCount} of {v.ItemCount} items priced";
        }
        UpdateValueChange();
        foreach (var r in Recent) r.SetNow(s.Prices.GetCached(r.Record.Item.MarketHashName));
        if (w.LastSuccessAt is { } last && (Points.Count == 0 || last > Points[^1].At)) ReloadChart();
    }

    /// <summary>Change against the last value point from at least 24 hours ago, in the current currency.</summary>
    private void UpdateValueChange()
    {
        var w = s.Watcher;
        var id = s.Settings.SteamId64;
        var cur = s.Settings.Currency;
        ValueChange = ValueChangeTrend = "";
        // Same coverage rule as the value history, so a half-priced total right after start-up isn't shown as a crash.
        if (id == 0 || w.Current is null || w.Value.ItemCount == 0 || w.Value.PricedCount < w.Value.ItemCount * 0.4) return;
        ValuePoint? then;
        try { then = s.Db.GetLastValuePoint(id, cur, DateTimeOffset.UtcNow.AddHours(-24)); }
        catch (Exception) { return; }
        if (then is null || then.Total <= 0) return;
        var delta = w.Value.Total - then.Total;
        var pct = delta / then.Total * 100m;
        var sign = delta > 0 ? "+" : delta < 0 ? "-" : "±";
        ValueChange = $"{sign}{Money.Format(Math.Abs(delta), cur)} ({sign}{Math.Abs(pct).ToString("0.0", CultureInfo.InvariantCulture)}%) · 24 h";
        ValueChangeTrend = ProfitFigures.Trend(Math.Round(delta, 2));
    }

    private void UpdateWeeklyStatus()
    {
        if (s.Settings.SteamId64 == 0)
        {
            WeeklyStatus = WeeklyStatusTip = WeeklyStatusKind = "";
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var st = WeeklyStreak.Compute(_drops, now);
        var left = When.Left(st.NextReset - now);
        var streak = st.Streak >= 2 ? $" · {st.Streak}-week streak" : "";
        if (st.ThisWeek)
        {
            WeeklyStatus = "This week's drop is in" + streak;
            WeeklyStatusKind = "up";
        }
        else
        {
            WeeklyStatus = st.AtRisk && st.Streak >= 2 ? $"No drop yet · {st.Streak}-week streak at risk" : $"Not yet this week · resets in {left}";
            WeeklyStatusKind = "warn";
        }
        WeeklyStatusTip = $"CS2 weekly drops reset every Wednesday at 01:00 UTC. Next reset in {left}: {When.Format(st.NextReset)} {st.NextReset.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}."
            + (st.Streak >= 2 ? $" You've had a weekly drop {st.Streak} weeks in a row." : "");
    }

    public void RefreshWhen()
    {
        UpdateWeeklyStatus();
        if (Recent.Count == 0) return;
        var rows = Recent.ToList();
        Recent.Clear();
        foreach (var r in rows) Recent.Add(new DropRowViewModel(r.Record, s.Prices.GetCached(r.Record.Item.MarketHashName)));
    }

    public void OnDropPriced(long id, PriceQuote? q)
    {
        var i = _drops.FindIndex(d => d.Id == id);
        if (i >= 0 && q is not null) _drops[i] = _drops[i] with { PriceAtDrop = q.Price, Currency = q.Currency, PriceSource = q.SourceId };
        var row = Recent.FirstOrDefault(r => r.Id == id);
        if (row is not null)
        {
            row.SetDropPrice(q);
            row.SetNow(q ?? s.Prices.GetCached(row.Record.Item.MarketHashName));
        }
        if (i >= 0) Reload();
    }

    private void ReloadChart()
    {
        var id = s.Settings.SteamId64;
        var cur = s.Settings.Currency;
        Currency = cur;
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? from = Range switch
        {
            "7D" => now.AddDays(-7),
            "90D" => now.AddDays(-90),
            "1Y" => now.AddYears(-1),
            "All" => null,
            _ => now.AddDays(-30),
        };
        if (id == 0)
        {
            Points = [];
            Markers = [];
            From = from;
            return;
        }
        List<ValuePoint> pts;
        try { pts = s.Db.GetValueHistory(id, from?.AddDays(-400) ?? DateTimeOffset.UnixEpoch, cur); }
        catch (Exception) { pts = new(); }
        var markers = _drops
            .Where(d => from is null || d.SeenAt >= from)
            .Take(400)
            .Select(d =>
            {
                var price = d.PriceAtDrop is { } p && d.Currency == cur ? " " + Money.Format(p, cur) : "";
                var big = d.Item.IsStar || (d.PriceAtDrop is { } q && d.Currency == cur && q >= 100);
                return new ChartMarker(d.SeenAt, (d.Item.Name + price).Trim(), ArrivalKindText.Label(d.Kind), big);
            })
            .ToList();
        From = from;
        Points = pts;
        Markers = markers;
    }

    [RelayCommand]
    private void SetRange(string range) => Range = range;
}

public sealed partial class DropsViewModel(AppServices s) : ObservableObject
{
    private static readonly (string Key, string Label)[] Groups =
    [
        ("all", "All"), ("drops", "Weekly drops"), ("openings", "Case openings"), ("tradeups", "Trade-ups"), ("purchases", "Bought / traded"),
    ];

    private List<DropRowViewModel> _all = new();
    private string _selected = "all";

    public ObservableCollection<FilterChip> Filters { get; } = new();

    [ObservableProperty] private IReadOnlyList<DropRowViewModel> _rows = [];
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private DropRowViewModel? _selectedRow;
    [ObservableProperty] private ItemDetailsViewModel? _details;

    partial void OnSearchChanged(string value) => Apply();

    partial void OnSelectedRowChanged(DropRowViewModel? value) =>
        Details = value is null ? null : ItemDetailsViewModel.For(s, value.Record.Item, value.Record);

    [RelayCommand]
    private void CloseDetails() => SelectedRow = null;

    private static string GroupKey(ArrivalKind k) =>
        ArrivalKindText.IsDropLike(k) ? "drops"
        : ArrivalKindText.IsOpening(k) ? "openings"
        : k == ArrivalKind.TradeUp ? "tradeups"
        : ArrivalKindText.IsPurchase(k) ? "purchases"
        : "other";

    public void Reload()
    {
        var id = s.Settings.SteamId64;
        List<DropRecord> drops;
        try { drops = id == 0 ? new() : s.Db.GetDrops(id, 50000); }
        catch (Exception) { drops = new(); }
        _all = drops.Select(d => new DropRowViewModel(d, s.Prices.GetCached(d.Item.MarketHashName))).ToList();
        var counts = _all.GroupBy(r => GroupKey(r.Record.Kind)).ToDictionary(g => g.Key, g => g.Count());
        Filters.Clear();
        foreach (var (key, label) in Groups)
        {
            var n = key == "all" ? _all.Count : counts.GetValueOrDefault(key);
            if (key != "all" && n == 0) continue;
            Filters.Add(new FilterChip(key, label) { Count = n });
        }
        if (Filters.All(f => f.Key != _selected)) _selected = "all";
        foreach (var f in Filters) f.IsSelected = f.Key == _selected;
        Apply();
    }

    public void RefreshNow()
    {
        foreach (var r in _all) r.SetNow(s.Prices.GetCached(r.Record.Item.MarketHashName));
    }

    public void OnDropPriced(long id, PriceQuote? q)
    {
        var row = _all.FirstOrDefault(r => r.Id == id);
        if (row is null) return;
        row.SetDropPrice(q);
        row.SetNow(q ?? s.Prices.GetCached(row.Record.Item.MarketHashName));
        UpdateSummary();
    }

    [RelayCommand]
    private void SelectFilter(FilterChip chip)
    {
        _selected = chip.Key;
        foreach (var f in Filters) f.IsSelected = f == chip;
        Apply();
    }

    private void Apply()
    {
        IEnumerable<DropRowViewModel> q = _all;
        if (_selected != "all") q = q.Where(r => GroupKey(r.Record.Kind) == _selected);
        var term = Search.Trim();
        if (term.Length > 0) q = q.Where(r => r.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || r.How.Contains(term, StringComparison.OrdinalIgnoreCase));
        Rows = q.ToList();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var list = Rows;
        var cur = s.Settings.Currency;
        var sum = list.Where(r => r.Record.PriceAtDrop.HasValue && r.Record.Currency == cur).Sum(r => r.Record.PriceAtDrop!.Value);
        var priced = list.Count(r => r.Record.PriceAtDrop.HasValue && r.Record.Currency == cur);
        Summary = list.Count == 0 ? "" : $"{list.Count} item{(list.Count == 1 ? "" : "s")} · {Money.Format(sum, cur)} at drop time" + (priced < list.Count ? $" · {list.Count - priced} without a price" : "");
    }
}
