using System.Net.Http.Json;
using Cs2DropChecker.Core.Pricing;

namespace Cs2DropChecker.Core.Alerts;

public enum AlertKind
{
    /// <summary>Item price moved up or down by Percent from Base.</summary>
    ItemMove,
    ItemAbove,
    ItemBelow,
    InventoryAbove,
    InventoryBelow,
    /// <summary>Inventory value moved by Percent compared with 24 hours ago.</summary>
    InventoryMove24h,
}

public sealed record AlertRule
{
    public long Id { get; init; }
    public AlertKind Kind { get; init; }
    public string? MarketHashName { get; init; }
    public string? ItemName { get; init; }
    public string? IconUrl { get; init; }
    public decimal? Percent { get; init; }
    public decimal? Target { get; init; }
    /// <summary>Price the move is measured from, for <see cref="AlertKind.ItemMove"/>.</summary>
    public decimal? Base { get; init; }
    public string Currency { get; init; } = "EUR";
    /// <summary>False after the alert fired, until the value moves back and resets it.</summary>
    public bool Armed { get; init; } = true;
    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FiredAt { get; init; }

    public bool IsInventory => Kind is AlertKind.InventoryAbove or AlertKind.InventoryBelow or AlertKind.InventoryMove24h;
}

public sealed record AlertCheck(bool Fire, bool Armed, string? Title = null, string? Body = null);

/// <summary>
/// Decides whether an alert fires. Each alert fires once and then waits until it resets: a price threshold resets when
/// the value is back on the other side by <see cref="ResetMargin"/>, a percentage move when it is back under half the
/// percentage.
/// </summary>
public static class AlertEvaluator
{
    public const decimal ResetMargin = 0.02m;

    /// <param name="current">Current item price or inventory total, in <paramref name="currency"/>.</param>
    /// <param name="reference">Inventory total 24 hours ago, for <see cref="AlertKind.InventoryMove24h"/>.</param>
    public static AlertCheck Evaluate(AlertRule r, decimal? current, string currency, decimal? reference = null)
    {
        if (!r.Enabled || current is not { } now || r.Currency != currency) return new(false, r.Armed);
        var subject = r.IsInventory ? "Inventory value" : r.ItemName ?? r.MarketHashName ?? "Item";
        switch (r.Kind)
        {
            case AlertKind.ItemAbove or AlertKind.InventoryAbove when r.Target is { } t:
                if (r.Armed && now >= t)
                    return new(true, false, $"{subject} is above {Fmt(t, currency)}", $"Now {Fmt(now, currency)}.");
                return new(false, r.Armed || now < t * (1 - ResetMargin));
            case AlertKind.ItemBelow or AlertKind.InventoryBelow when r.Target is { } t:
                if (r.Armed && now <= t)
                    return new(true, false, $"{subject} is below {Fmt(t, currency)}", $"Now {Fmt(now, currency)}.");
                return new(false, r.Armed || now > t * (1 + ResetMargin));
            case AlertKind.ItemMove when r.Percent is { } p && r.Base is { } b && b > 0:
                return Move(r, subject, now, b, p, currency, "since the alert was set");
            case AlertKind.InventoryMove24h when r.Percent is { } p && reference is { } refValue && refValue > 0:
                return Move(r, subject, now, refValue, p, currency, "in 24 h");
            default:
                return new(false, r.Armed);
        }
    }

    public static decimal ChangePercent(decimal from, decimal to) => from == 0 ? 0 : (to - from) / from * 100m;

    private static AlertCheck Move(AlertRule r, string subject, decimal now, decimal from, decimal percent, string currency, string period)
    {
        var change = ChangePercent(from, now);
        if (r.Armed && Math.Abs(change) >= percent)
        {
            var dir = change > 0 ? "up" : "down";
            return new(true, false, $"{subject} is {dir} {Math.Abs(change):0.#}% {period}", $"{Fmt(from, currency)} → {Fmt(now, currency)}.");
        }
        return new(false, r.Armed || Math.Abs(change) < percent / 2);
    }

    private static string Fmt(decimal v, string c) => Currencies.Format(v, c);

    public static string Describe(AlertRule r) => r.Kind switch
    {
        AlertKind.ItemMove => $"Moves ±{r.Percent:0.#}% from {Fmt(r.Base ?? 0, r.Currency)}",
        AlertKind.ItemAbove => $"Rises to {Fmt(r.Target ?? 0, r.Currency)}",
        AlertKind.ItemBelow => $"Falls to {Fmt(r.Target ?? 0, r.Currency)}",
        AlertKind.InventoryAbove => $"Total rises to {Fmt(r.Target ?? 0, r.Currency)}",
        AlertKind.InventoryBelow => $"Total falls to {Fmt(r.Target ?? 0, r.Currency)}",
        _ => $"Total moves ±{r.Percent:0.#}% in 24 h",
    };
}

/// <summary>Posts alerts to a Discord webhook. The URL is a secret: it is never logged or put in exception messages.</summary>
public sealed class DiscordWebhook(HttpClient http)
{
    public static bool IsValidUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && u.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com"
        && u.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);

    /// <summary>Returns the HTTP status, or -1 when Discord could not be reached.</summary>
    public async Task<int> SendAsync(string url, string title, string body, CancellationToken ct)
    {
        if (!IsValidUrl(url)) return -1;
        var payload = new
        {
            username = "CS2 Drop Checker",
            content = $"**{title}**\n{body}",
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
        try
        {
            using var resp = await http.PostAsJsonAsync(url.Trim(), payload, ct);
            return (int)resp.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return -1;
        }
    }
}
