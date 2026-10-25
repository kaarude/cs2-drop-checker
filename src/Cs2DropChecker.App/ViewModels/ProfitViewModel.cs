using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Profit;

namespace Cs2DropChecker.App.ViewModels;

/// <summary>Lifetime, week and month case results for the watched account.</summary>
public sealed record ProfitFigures(ProfitSummary Lifetime, ProfitSummary Week, ProfitSummary Month, IReadOnlyList<CaseOpening> Openings, string Currency)
{
    public static ProfitFigures Load(AppServices s)
    {
        var cur = s.Settings.Currency;
        var id = s.Settings.SteamId64;
        List<CaseOpening> openings;
        try { openings = id == 0 ? new() : s.Db.GetOpenings(id); }
        catch (Exception) { openings = new(); }
        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var month = new DateTime(today.Year, today.Month, 1);
        return new ProfitFigures(
            ProfitCalculator.Summarize(openings, cur),
            ProfitCalculator.Summarize(openings, cur, new DateTimeOffset(monday)),
            ProfitCalculator.Summarize(openings, cur, new DateTimeOffset(month)),
            openings,
            cur);
    }

    public static string Trend(decimal? net) => net is null ? "" : net > 0 ? "up" : net < 0 ? "down" : "";

    public string Headline => Lifetime.Counted == 0 ? "No priced openings yet" : ProfitCalculator.Headline(Lifetime.Net, Currency);

    public string Signed(ProfitSummary p) => p.Counted == 0 ? "—" : ProfitCalculator.Signed(p.Net, Currency);

    public string Roi => Lifetime.RoiPercent is { } r ? (r > 0 ? "+" : "") + r.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";
}

public sealed record CaseStatRow(string Name, int Count, string Spent, string Returned, string Net, string Roi, string Trend);

public sealed partial class OpeningRow : ObservableObject
{
    public OpeningRow(CaseOpening o, string currency)
    {
        Opening = o;
        When = Services.When.Format(o.OpenedAt);
        IconUrl = o.Item.IconUrl;
        Name = o.Item.Name;
        RarityHex = o.Item.RarityHex;
        Container = o.ContainerName ?? (o.Backfilled ? "Case unknown" : "Not detected");
        NeedsCase = !o.ContainerKnown;
        var c = o.Currency ?? currency;
        CaseText = o.CasePrice is { } cp ? Money.Format(cp, c) : "—";
        KeyText = !o.NeedsKey ? "no key" : o.KeyPrice is { } kp ? Money.Format(kp, c) : "—";
        ItemPriceText = o.ItemPrice is { } ip ? Money.Format(ip, o.ItemCurrency) : "—";
        var net = o.IsComplete(currency) ? o.Net : null;
        NetText = net is { } n ? ProfitCalculator.Signed(n, currency) : o.ContainerKnown && o.Currency is { } oc && oc != currency ? "other currency" : "—";
        Trend = ProfitFigures.Trend(net);
        Estimate = o.Backfilled && o.ContainerKnown;
    }

    public CaseOpening Opening { get; }
    public string When { get; }
    public string IconUrl { get; }
    public string Name { get; }
    public string RarityHex { get; }
    public string Container { get; }
    public bool NeedsCase { get; }
    public bool Estimate { get; }
    public string CaseText { get; }
    public string KeyText { get; }
    public string ItemPriceText { get; }
    public string NetText { get; }
    public string Trend { get; }

    [ObservableProperty] private Option? _pick;
}

public sealed partial class ProfitViewModel(AppServices s) : ObservableObject
{
    [ObservableProperty] private string _headline = "No priced openings yet";
    [ObservableProperty] private string _headlineTrend = "";
    [ObservableProperty] private string _lifetimeSub = "";
    [ObservableProperty] private string _week = "—";
    [ObservableProperty] private string _weekTrend = "";
    [ObservableProperty] private string _weekSub = "";
    [ObservableProperty] private string _month = "—";
    [ObservableProperty] private string _monthTrend = "";
    [ObservableProperty] private string _monthSub = "";
    [ObservableProperty] private string _roi = "—";
    [ObservableProperty] private string _roiTrend = "";
    [ObservableProperty] private string _roiSub = "";
    [ObservableProperty] private string _best = "—";
    [ObservableProperty] private string _bestSub = "";
    [ObservableProperty] private string _worst = "—";
    [ObservableProperty] private string _worstSub = "";
    [ObservableProperty] private string _incomplete = "";
    [ObservableProperty] private string _keyNote = "";
    [ObservableProperty] private int _total;

    public ObservableCollection<CaseStatRow> Cases { get; } = new();
    public ObservableCollection<OpeningRow> Openings { get; } = new();
    public ObservableCollection<Option> KnownContainers { get; } = new();

    public void Reload()
    {
        var f = ProfitFigures.Load(s);
        var cur = f.Currency;
        var life = f.Lifetime;
        Total = f.Openings.Count;
        Headline = f.Headline;
        HeadlineTrend = life.Counted == 0 ? "" : ProfitFigures.Trend(life.Net);
        LifetimeSub = life.Counted == 0 ? "Open a case and the result shows up here"
            : $"{life.Counted} opening{(life.Counted == 1 ? "" : "s")} · spent {Money.Format(life.Spent, cur)} · got {Money.Format(life.Returned, cur)}";
        Week = f.Signed(f.Week);
        WeekTrend = f.Week.Counted == 0 ? "" : ProfitFigures.Trend(f.Week.Net);
        WeekSub = $"{f.Week.Counted} since Monday";
        Month = f.Signed(f.Month);
        MonthTrend = f.Month.Counted == 0 ? "" : ProfitFigures.Trend(f.Month.Net);
        MonthSub = $"{f.Month.Counted} this month";
        Roi = f.Roi;
        RoiTrend = life.RoiPercent is { } r ? ProfitFigures.Trend(r) : "";
        RoiSub = "Return on case + key spend";
        if (life.Best is { } b)
        {
            Best = ProfitCalculator.Signed(b.Net!.Value, cur);
            BestSub = $"{b.Item.Name} · {b.ContainerName}";
        }
        else
        {
            Best = "—";
            BestSub = "";
        }
        if (life.Worst is { } w)
        {
            Worst = ProfitCalculator.Signed(w.Net!.Value, cur);
            WorstSub = $"{w.Item.Name} · {w.ContainerName}";
        }
        else
        {
            Worst = "—";
            WorstSub = "";
        }
        var missing = f.Openings.Count(o => !o.ContainerKnown);
        Incomplete = life.Incomplete == 0 ? ""
            : $"{life.Incomplete} of {life.Openings} openings aren't counted yet"
              + (missing > 0 ? $": {missing} have no case recorded (pick it below to estimate the cost)" : ": a price is missing or was recorded in another currency")
              + ".";
        var key = s.KeyPriceIn(cur);
        KeyNote = (s.Settings.Profit.KeyPrice is null ? "Key price: Valve store price" : "Key price: your setting")
            + (key is { } k ? $" · {Money.Format(k, cur)}" : "") + " · change it in Settings";

        Cases.Clear();
        foreach (var c in life.ByContainer)
            Cases.Add(new CaseStatRow(c.Name.Length > 0 ? c.Name : "Unknown", c.Count, Money.Format(c.Spent, cur), Money.Format(c.Returned, cur),
                ProfitCalculator.Signed(c.Net, cur), c.RoiPercent is { } cr ? (cr > 0 ? "+" : "") + cr.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—",
                ProfitFigures.Trend(c.Net)));

        KnownContainers.Clear();
        var names = f.Openings.Where(o => o.ContainerKnown).Select(o => (o.ContainerName ?? o.ContainerHashName!, o.ContainerHashName!))
            .Concat(s.Watcher.Current?.Items.Where(i => i.IsContainer).Select(i => (i.Name, i.MarketHashName)) ?? [])
            .DistinctBy(x => x.Item2)
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hash) in names) KnownContainers.Add(new Option(hash, name));

        Openings.Clear();
        foreach (var o in f.Openings.Take(300))
        {
            var row = new OpeningRow(o, cur);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OpeningRow.Pick) && row.Pick is { } pick) AssignCase(row, pick);
            };
            Openings.Add(row);
        }
    }

    /// <summary>Names the case of an opening recorded before cost tracking, priced at today's case and key price.</summary>
    private void AssignCase(OpeningRow row, Option pick)
    {
        var hash = (string)pick.Value;
        var cur = s.Settings.Currency;
        var q = s.Prices.GetCached(hash);
        var key = KeyPrices.NeedsKey(row.Opening.Kind) ? s.KeyPriceIn(cur) : null;
        try { s.Db.UpdateOpeningCost(row.Opening.DropId, pick.Label, hash, q?.Currency == cur ? q.Price : null, key, cur); }
        catch (Exception) { return; }
        Reload();
    }

    [RelayCommand]
    private void Refresh() => Reload();
}
