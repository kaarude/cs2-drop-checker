using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Alerts;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;

namespace Cs2DropChecker.App.ViewModels;

public sealed record AlertKindOption(AlertKind Kind, string Label, bool NeedsItem, bool IsPercent)
{
    public override string ToString() => Label;
}

public sealed partial class AlertRow : ObservableObject
{
    private readonly Action<AlertRow, bool> _onEnabled;
    private bool _init = true;

    public AlertRow(AlertRule r, PriceQuote? now, decimal? total, Action<AlertRow, bool> onEnabled)
    {
        Rule = r;
        _onEnabled = onEnabled;
        Subject = r.IsInventory ? "Inventory value" : r.ItemName ?? r.MarketHashName ?? "Item";
        IconUrl = r.IconUrl ?? "";
        Rule2 = AlertEvaluator.Describe(r);
        var current = r.IsInventory ? total : now?.Price;
        Now = current is { } c ? "Now " + Money.Format(c, r.Currency) : r.IsInventory ? "Inventory not loaded" : "No price yet";
        State = !r.Armed ? "Fired " + When.Ago(r.FiredAt) + " · waits until the price moves back" : "Watching";
        StateKind = r.Armed ? "ok" : "warn";
        Enabled = r.Enabled;
        _init = false;
    }

    public AlertRule Rule { get; }
    public string Subject { get; }
    public string IconUrl { get; }
    public string Rule2 { get; }
    public string Now { get; }
    public string State { get; }
    public string StateKind { get; }
    public bool Fired => !Rule.Armed;
    [ObservableProperty] private bool _enabled;

    partial void OnEnabledChanged(bool value)
    {
        if (!_init) _onEnabled(this, value);
    }
}

public sealed partial class AlertsViewModel : ObservableObject
{
    private readonly AppServices _s;

    public AlertsViewModel(AppServices s)
    {
        _s = s;
        _kind = Kinds[0];
        s.Alerts.Changed += (_, _) => Reload();
    }

    public IReadOnlyList<AlertKindOption> Kinds { get; } =
    [
        new(AlertKind.ItemMove, "Item moves by ±%", true, true),
        new(AlertKind.ItemAbove, "Item rises to a price", true, false),
        new(AlertKind.ItemBelow, "Item falls to a price", true, false),
        new(AlertKind.InventoryAbove, "Inventory total rises to", false, false),
        new(AlertKind.InventoryBelow, "Inventory total falls to", false, false),
        new(AlertKind.InventoryMove24h, "Inventory total moves ±% in 24 h", false, true),
    ];

    public ObservableCollection<Option> Items { get; } = new();
    public ObservableCollection<AlertRow> Rows { get; } = new();

    [ObservableProperty] private AlertKindOption _kind;
    [ObservableProperty] private Option? _item;
    [ObservableProperty] private string _amount = "10";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _messageKind = "";
    [ObservableProperty] private string _delivery = "";

    public bool NeedsItem => Kind.NeedsItem;
    public string AmountLabel => Kind.IsPercent ? "Percent" : $"Price ({Currencies.Get(_s.Settings.Currency).Symbol.Trim()})";

    partial void OnKindChanged(AlertKindOption value)
    {
        OnPropertyChanged(nameof(NeedsItem));
        OnPropertyChanged(nameof(AmountLabel));
        SuggestAmount();
    }

    partial void OnItemChanged(Option? value) => SuggestAmount();

    private void SuggestAmount()
    {
        if (Kind.IsPercent) Amount = Kind.Kind == AlertKind.InventoryMove24h ? "5" : "10";
        else if (Kind.NeedsItem && Item?.Value is string hash && _s.Prices.GetCached(hash) is { } q)
            Amount = Math.Round(q.Price * (Kind.Kind == AlertKind.ItemAbove ? 1.1m : 0.9m), 2).ToString("0.00", CultureInfo.InvariantCulture);
        else if (!Kind.NeedsItem && _s.Watcher.Current is not null)
            Amount = Math.Round(_s.Watcher.Value.Total * (Kind.Kind == AlertKind.InventoryAbove ? 1.1m : 0.9m)).ToString("0", CultureInfo.InvariantCulture);
    }

    public void Reload()
    {
        var cur = _s.Settings.Currency;
        var a = _s.Settings.Alerts;
        var discord = DiscordWebhook.IsValidUrl(a.DiscordWebhookUrl);
        Delivery = (a.Toasts ? "Windows notifications on" : "Windows notifications off") + (discord ? " · Discord webhook on" : " · Discord off")
            + " · change in Settings. Checked whenever prices refresh.";
        OnPropertyChanged(nameof(AmountLabel));

        var selected = Item?.Value as string;
        Items.Clear();
        var items = _s.Watcher.Current?.Items ?? [];
        foreach (var g in items.Where(i => i.Marketable || i.Tradable || i.IsContainer).GroupBy(i => i.MarketHashName)
                     .Select(g => (Item: g.First(), Count: g.Count(), Price: _s.Prices.GetCached(g.Key)))
                     .OrderByDescending(x => x.Price?.Price ?? 0))
        {
            var label = g.Item.Name + (g.Count > 1 ? $" ×{g.Count}" : "") + (g.Price is { } p ? " · " + Money.Format(p) : "");
            Items.Add(new Option(g.Item.MarketHashName, label));
        }
        if (selected is not null) Item = Items.FirstOrDefault(o => (string)o.Value == selected) ?? Item;

        List<AlertRule> rules;
        try { rules = _s.Db.GetAlerts(); }
        catch (Exception) { rules = new(); }
        var w = _s.Watcher;
        decimal? total = w.Current is null ? null : w.Value.Total;
        Rows.Clear();
        foreach (var r in rules)
            Rows.Add(new AlertRow(r, r.MarketHashName is { } n ? _s.Prices.GetCached(n) : null, r.Currency == cur ? total : null, OnEnabled));
    }

    /// <summary>Opens the form for one item, also for items outside the inventory (Lookup page).</summary>
    public void PrepareFor(InventoryItem item)
    {
        Reload();
        var opt = Items.FirstOrDefault(o => (string)o.Value == item.MarketHashName);
        if (opt is null)
        {
            opt = new Option(item.MarketHashName, item.Name);
            Items.Insert(0, opt);
        }
        if (!Kind.NeedsItem) Kind = Kinds[0];
        Item = opt;
        Message = "";
    }

    private void OnEnabled(AlertRow row, bool enabled)
    {
        try { _s.Db.SetAlertEnabled(row.Rule.Id, enabled); }
        catch (Exception) { }
    }

    public static decimal? ParseAmount(string text)
    {
        var t = text.Trim().TrimEnd('%').Trim().Replace(',', '.');
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }

    [RelayCommand]
    private void Add()
    {
        var cur = _s.Settings.Currency;
        if (ParseAmount(Amount) is not { } amount)
        {
            Message = Kind.IsPercent ? "Enter a percentage above 0." : "Enter a price above 0.";
            MessageKind = "warn";
            return;
        }
        if (Kind.IsPercent && amount > 1000)
        {
            Message = "Use a percentage up to 1000.";
            MessageKind = "warn";
            return;
        }
        string? hash = null, name = null, icon = null;
        decimal? basePrice = null;
        if (Kind.NeedsItem)
        {
            if (Item?.Value is not string h)
            {
                Message = "Pick an item first.";
                MessageKind = "warn";
                return;
            }
            hash = h;
            var item = _s.Watcher.Current?.Items.FirstOrDefault(i => i.MarketHashName == h);
            name = item?.Name ?? Item.Label.Split(" · ")[0];
            icon = item?.IconUrl;
            var q = _s.Prices.GetCached(h);
            if (Kind.Kind == AlertKind.ItemMove)
            {
                if (q is null)
                {
                    Message = "This item has no price yet, so there's nothing to measure the move from.";
                    MessageKind = "warn";
                    return;
                }
                basePrice = q.Price;
            }
        }
        var rule = new AlertRule
        {
            Kind = Kind.Kind,
            MarketHashName = hash,
            ItemName = name,
            IconUrl = icon,
            Percent = Kind.IsPercent ? amount : null,
            Target = Kind.IsPercent ? null : amount,
            Base = basePrice,
            Currency = cur,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        try { _s.Db.AddAlert(rule); }
        catch (Exception ex)
        {
            Message = "Could not save the alert: " + ex.Message;
            MessageKind = "warn";
            return;
        }
        Message = "Alert added: " + AlertEvaluator.Describe(rule) + ".";
        MessageKind = "ok";
        Reload();
        _s.Alerts.Evaluate();
    }

    [RelayCommand]
    private void Reset(AlertRow row)
    {
        decimal? newBase = row.Rule.Kind == AlertKind.ItemMove && row.Rule.MarketHashName is { } n ? _s.Prices.GetCached(n)?.Price : null;
        try { _s.Db.ResetAlert(row.Rule.Id, newBase); }
        catch (Exception) { }
        Reload();
    }

    [RelayCommand]
    private void Delete(AlertRow row)
    {
        try { _s.Db.DeleteAlert(row.Rule.Id); }
        catch (Exception) { }
        Reload();
    }
}
