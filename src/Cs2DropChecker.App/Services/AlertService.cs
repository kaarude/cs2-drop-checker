using Cs2DropChecker.Core.Alerts;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Services;

/// <summary>Checks price alerts whenever prices or the inventory value refresh, and delivers the ones that fire.</summary>
public sealed class AlertService
{
    private readonly AppServices _s;
    private readonly ILogger _log;
    private readonly DiscordWebhook _discord;
    private bool _queued;

    public AlertService(AppServices s, ILogger log)
    {
        _s = s;
        _log = log;
        _discord = new DiscordWebhook(s.Http);
    }

    /// <summary>Raised on the UI thread after an alert fired or changed state.</summary>
    public event EventHandler? Changed;

    public void Start() => _s.Watcher.InventoryChanged += (_, _) => Queue();

    private void Queue()
    {
        if (_queued) return;
        _queued = true;
        _s.Ui.BeginInvoke(() =>
        {
            _queued = false;
            Evaluate();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Evaluate()
    {
        List<AlertRule> rules;
        try { rules = _s.Db.GetAlerts(); }
        catch (Exception ex)
        {
            _log.LogWarning("Could not load alerts: {Message}", ex.Message);
            return;
        }
        if (rules.Count == 0) return;
        var cur = _s.Settings.Currency;
        var now = DateTimeOffset.UtcNow;
        var w = _s.Watcher;
        var v = w.Value;
        decimal? total = w.Current is not null && v.ItemCount > 0 && v.PricedCount >= v.ItemCount * 0.4 ? v.Total : null;
        decimal? dayAgo = null;
        var changed = false;
        foreach (var r in rules.Where(r => r.Enabled))
        {
            decimal? current = r.IsInventory ? total : r.MarketHashName is { } name ? _s.Prices.GetCached(name)?.Price : null;
            if (r.Kind == AlertKind.InventoryMove24h && dayAgo is null && total is not null && _s.Settings.SteamId64 != 0)
            {
                try { dayAgo = _s.Db.GetLastValuePoint(_s.Settings.SteamId64, cur, now.AddHours(-24))?.Total; }
                catch (Exception) { }
            }
            var check = AlertEvaluator.Evaluate(r, current, cur, dayAgo);
            try
            {
                if (check.Fire)
                {
                    _s.Db.UpdateAlertState(r.Id, false, now);
                    Deliver(check.Title ?? "Price alert", check.Body ?? "");
                    changed = true;
                }
                else if (check.Armed != r.Armed)
                {
                    _s.Db.UpdateAlertState(r.Id, check.Armed, r.FiredAt);
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not update an alert: {Message}", ex.Message);
            }
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Deliver(string title, string body)
    {
        var a = _s.Settings.Alerts;
        _log.LogInformation("Alert fired");
        if (a.Toasts) _s.Notifier.Show(title, body);
        if (DiscordWebhook.IsValidUrl(a.DiscordWebhookUrl)) _ = SendDiscordAsync(a.DiscordWebhookUrl, title, body);
    }

    /// <summary>Returns the HTTP status Discord answered with, or -1.</summary>
    public async Task<int> SendDiscordAsync(string url, string title, string body)
    {
        var status = await _discord.SendAsync(url, title, body, CancellationToken.None);
        if (status is < 200 or >= 300) _log.LogInformation("Discord webhook answered {Status}", status);
        return status;
    }

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
