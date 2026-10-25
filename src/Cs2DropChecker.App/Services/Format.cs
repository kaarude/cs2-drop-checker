using System.Globalization;
using Cs2DropChecker.Core.Pricing;

namespace Cs2DropChecker.App.Services;

public static class Money
{
    public static string Format(decimal value, string? currency) => Currencies.Format(value, currency);

    public static string Format(PriceQuote? q) => q is null ? "—" : Currencies.Format(q.Price, q.Currency);

    public static string Short(decimal value, string? currency)
    {
        var c = Currencies.Get(currency);
        var n = value >= 1000
            ? (value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k"
            : Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        return c.SymbolAfter ? n + c.Symbol : c.Symbol + n;
    }
}

public static class When
{
    public static string Format(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = DateTime.Today;
        var inv = CultureInfo.InvariantCulture;
        if (local.Date == today) return "Today " + local.ToString("HH:mm", inv);
        if (local.Date == today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm", inv);
        if (local.Year == today.Year) return local.ToString("MMM d", inv);
        return local.ToString("MMM d, yyyy", inv);
    }

    public static string Short(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span < TimeSpan.FromSeconds(60)) return $"{(int)Math.Ceiling(span.TotalSeconds)} s";
        if (span < TimeSpan.FromHours(1)) return $"{(int)Math.Ceiling(span.TotalMinutes)} min";
        return $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    public static string Ago(DateTimeOffset? at)
    {
        if (at is null) return "never";
        var span = DateTimeOffset.UtcNow - at.Value;
        if (span < TimeSpan.FromSeconds(5)) return "just now";
        if (span < TimeSpan.FromMinutes(1)) return $"{(int)span.TotalSeconds} s ago";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} d ago";
    }
}
