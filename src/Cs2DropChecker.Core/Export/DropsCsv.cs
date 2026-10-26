using System.Globalization;
using System.Text;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;

namespace Cs2DropChecker.Core.Export;

/// <summary>Writes the Drops history as CSV (RFC 4180) that Excel, LibreOffice and Google Sheets open as-is.</summary>
public static class DropsCsv
{
    public static readonly string[] Header =
    [
        "Seen at (UTC)", "Kind", "Item", "Market hash name", "Rarity", "Exterior", "Float", "Pattern",
        "StatTrak", "Souvenir", "Stickers", "Price at drop", "Currency", "Price source", "Price now", "Now currency", "Seen late",
    ];

    /// <param name="now">Current price per market hash name, or null when there is none.</param>
    public static string Write(IEnumerable<DropRecord> drops, Func<string, PriceQuote?>? now = null)
    {
        var sb = new StringBuilder();
        AppendRow(sb, Header);
        foreach (var d in drops)
        {
            var i = d.Item;
            var q = now?.Invoke(i.MarketHashName);
            AppendRow(sb,
            [
                d.SeenAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ArrivalKindText.Label(d.Kind),
                i.Name,
                i.MarketHashName,
                i.IsStar ? "Knife / gloves" : i.RarityName,
                i.Exterior,
                i.Float?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "",
                i.PaintSeed?.ToString(CultureInfo.InvariantCulture) ?? "",
                i.IsStatTrak ? "yes" : "",
                i.IsSouvenir ? "yes" : "",
                i.StickerCount > 0 ? i.StickerCount.ToString(CultureInfo.InvariantCulture) : "",
                d.PriceAtDrop?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
                d.PriceAtDrop.HasValue ? d.Currency ?? "" : "",
                d.PriceSource is null ? "" : PriceSourceIds.TableName(d.PriceSource),
                q?.Price.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
                q?.Currency ?? "",
                d.SeenLate ? "yes" : "",
            ]);
        }
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells)
    {
        for (var c = 0; c < cells.Count; c++)
        {
            if (c > 0) sb.Append(',');
            sb.Append(Escape(cells[c]));
        }
        sb.Append("\r\n");
    }

    /// <summary>
    /// Quotes fields that need it. Text that a spreadsheet would run as a formula (=, +, -, @, tab, CR) gets a leading
    /// apostrophe; numbers are written by this class and never start that way except negatives, which are left alone.
    /// </summary>
    public static string Escape(string value)
    {
        if (value.Length == 0) return "";
        if (value[0] is '=' or '+' or '@' or '\t' or '\r' || (value[0] == '-' && !IsNumber(value)))
            value = "'" + value;
        var quote = value.AsSpan().IndexOfAny(",\"\r\n") >= 0;
        return quote ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static bool IsNumber(string s) => decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);

    /// <summary>"cs2-drops-2026-10-26.csv".</summary>
    public static string FileName(DateTimeOffset now) => $"cs2-drops-{now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv";
}
