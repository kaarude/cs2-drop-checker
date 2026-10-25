using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cs2DropChecker.Core.Market;
using Cs2DropChecker.Core.Pricing;

namespace Cs2DropChecker.App.Controls;

/// <summary>Steam median sale price over the last weeks, with sales volume and an optional line for the chosen price.</summary>
public sealed class PriceChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<PricePoint>), typeof(PriceChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrencyProperty = DependencyProperty.Register(
        nameof(Currency), typeof(string), typeof(PriceChart),
        new FrameworkPropertyMetadata("EUR", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerProperty = DependencyProperty.Register(
        nameof(Marker), typeof(decimal?), typeof(PriceChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(int), typeof(PriceChart),
        new FrameworkPropertyMetadata(30, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(PriceChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<PricePoint>? Points { get => (IReadOnlyList<PricePoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public string Currency { get => (string)GetValue(CurrencyProperty); set => SetValue(CurrencyProperty, value); }
    public decimal? Marker { get => (decimal?)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }
    public int Days { get => (int)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    private static readonly Brush AxisBrush = BrushCache.FromHex("7f8ea3");
    private static readonly Brush VolumeBrush = BrushCache.FromArgb(0x55, 0x5d, 0x79, 0xae);
    private static readonly Pen GridPen = Frozen(new Pen(BrushCache.FromArgb(0x2e, 0x5d, 0x79, 0xae), 1));
    private static readonly Pen LinePen = Frozen(new Pen(BrushCache.FromHex("de9b35"), 1.6) { LineJoin = PenLineJoin.Round });
    private static readonly Pen MarkerPen = Frozen(new Pen(BrushCache.FromHex("8fd19a"), 1.2) { DashStyle = DashStyles.Dash });
    private static readonly Pen CrossPen = Frozen(new Pen(BrushCache.FromArgb(0x66, 0xde, 0x9b, 0x35), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen TipPen = Frozen(new Pen(BrushCache.FromHex("de9b35"), 1));
    private static readonly Brush Fill = FrozenBrush(new LinearGradientBrush(
        Color.FromArgb(0x38, 0xde, 0x9b, 0x35), Color.FromArgb(0x00, 0xde, 0x9b, 0x35), 90));
    private static readonly Typeface Face = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private Point? _mouse;

    private static Pen Frozen(Pen p) { p.Freeze(); return p; }
    private static Brush FrozenBrush(Brush b) { b.Freeze(); return b; }

    public PriceChart()
    {
        ClipToBounds = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _mouse = null;
        InvalidateVisual();
    }

    private FormattedText Txt(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private sealed record Bucket(DateTimeOffset At, decimal Price, int Volume);

    /// <summary>Hourly medians are noisy; six-hour volume-weighted buckets read better over a month.</summary>
    private static List<Bucket> Bucketize(IEnumerable<PricePoint> points, TimeSpan size)
    {
        return points
            .GroupBy(p => p.At.UtcTicks / size.Ticks)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var vol = g.Sum(p => Math.Max(1, p.Volume));
                var price = g.Sum(p => p.Price * Math.Max(1, p.Volume)) / vol;
                return new Bucket(new DateTimeOffset(g.Key * size.Ticks + size.Ticks / 2, TimeSpan.Zero), price, g.Sum(p => p.Volume));
            })
            .ToList();
    }

    private string Axis(decimal v)
    {
        var code = Currency;
        if (v >= 100) return Currencies.Format(Math.Round(v), code).Replace(".00", "", StringComparison.Ordinal);
        return Currencies.Format(Math.Round(v, 2), code);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var now = DateTimeOffset.UtcNow;
        var t0 = now - TimeSpan.FromDays(Math.Max(1, Days));
        var raw = (Points ?? []).Where(p => p.At >= t0 && p.Price > 0).ToList();
        var buckets = Bucketize(raw, TimeSpan.FromHours(Days > 10 ? 6 : 1));

        var yLabelWidth = 0.0;
        decimal lo = 0, hi = 1;
        if (buckets.Count > 0)
        {
            lo = buckets.Min(b => b.Price);
            hi = buckets.Max(b => b.Price);
            if (Marker is { } m && m > 0 && m >= lo / 2 && m <= hi * 2)
            {
                lo = Math.Min(lo, m);
                hi = Math.Max(hi, m);
            }
            var pad = Math.Max((hi - lo) * 0.12m, hi * 0.02m + 0.01m);
            lo = Math.Max(0, lo - pad);
            hi += pad;
            yLabelWidth = new[] { lo, (lo + hi) / 2, hi }.Max(v => Txt(Axis(v), 10.5, AxisBrush).Width);
        }

        var left = Math.Max(40, yLabelWidth + 14);
        const double right = 10, top = 8, bottom = 22;
        var plot = new Rect(left, top, Math.Max(10, w - left - right), Math.Max(10, h - top - bottom));

        for (var i = 0; i <= 2; i++)
        {
            var gy = plot.Bottom - plot.Height * i / 2;
            dc.DrawLine(GridPen, new Point(plot.Left, gy), new Point(plot.Right, gy));
        }

        if (buckets.Count == 0)
        {
            var msg = Txt(EmptyText.ToUpperInvariant(), 10.5, AxisBrush);
            dc.DrawText(msg, new Point(plot.Left + (plot.Width - msg.Width) / 2, plot.Top + (plot.Height - msg.Height) / 2));
            return;
        }

        double X(DateTimeOffset t) => plot.Left + plot.Width * ((t - t0).TotalSeconds / Math.Max(1, (now - t0).TotalSeconds));
        double Y(decimal v) => plot.Bottom - plot.Height * (double)((v - lo) / Math.Max(0.0001m, hi - lo));

        for (var i = 0; i <= 2; i++)
        {
            var value = lo + (hi - lo) * i / 2;
            var label = Txt(Axis(value), 10.5, AxisBrush);
            dc.DrawText(label, new Point(plot.Left - 8 - label.Width, plot.Bottom - plot.Height * i / 2 - label.Height / 2));
        }
        for (var i = 0; i <= 4; i++)
        {
            var t = t0 + TimeSpan.FromTicks((now - t0).Ticks * i / 4);
            var label = Txt(t.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture), 10.5, AxisBrush);
            var lx = Math.Clamp(X(t) - label.Width / 2, plot.Left - 4, plot.Right - label.Width);
            dc.DrawText(label, new Point(lx, plot.Bottom + 6));
        }

        var maxVol = Math.Max(1, buckets.Max(b => b.Volume));
        var barW = Math.Max(1, plot.Width / Math.Max(1, (now - t0).TotalHours / (Days > 10 ? 6 : 1)) - 1);
        foreach (var b in buckets)
        {
            var bh = plot.Height * 0.28 * b.Volume / maxVol;
            if (bh < 0.5) continue;
            dc.DrawRectangle(VolumeBrush, null, new Rect(X(b.At) - barW / 2, plot.Bottom - bh, barW, bh));
        }

        var line = buckets.Select(b => new Point(X(b.At), Y(b.Price))).ToList();
        if (line.Count == 1) line.Insert(0, new Point(plot.Left, line[0].Y));
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(line[0].X, plot.Bottom), true, true);
            ctx.PolyLineTo(line, false, false);
            ctx.LineTo(new Point(line[^1].X, plot.Bottom), false, false);
        }
        area.Freeze();
        dc.DrawGeometry(Fill, null, area);
        var path = new StreamGeometry();
        using (var ctx = path.Open())
        {
            ctx.BeginFigure(line[0], false, false);
            ctx.PolyLineTo(line.Skip(1).ToList(), true, true);
        }
        path.Freeze();
        dc.DrawGeometry(null, LinePen, path);

        if (Marker is { } mk && mk >= lo && mk <= hi)
        {
            var my = Y(mk);
            dc.DrawLine(MarkerPen, new Point(plot.Left, my), new Point(plot.Right, my));
            var tag = Txt("YOUR PRICE " + Currencies.Format(mk, Currency), 9.5, BrushCache.FromHex("8fd19a"), true);
            var ty = my - tag.Height - 2 < plot.Top ? my + 2 : my - tag.Height - 2;
            dc.DrawText(tag, new Point(plot.Left + 6, ty));
        }

        if (_mouse is not { } mouse || mouse.X < plot.Left || mouse.X > plot.Right) return;
        var near = buckets.MinBy(b => Math.Abs(X(b.At) - mouse.X))!;
        var anchor = new Point(X(near.At), Y(near.Price));
        dc.DrawLine(CrossPen, new Point(anchor.X, plot.Top), new Point(anchor.X, plot.Bottom));
        dc.DrawEllipse(BrushCache.FromHex("f3b350"), null, anchor, 3.5, 3.5);
        var lines = new[]
        {
            Txt(near.At.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture), 10.5, BrushCache.FromHex("8fa0b6")),
            Txt(Currencies.Format(Math.Round(near.Price, 2), Currency), 12, BrushCache.FromHex("e6ebf1"), true),
            Txt(near.Volume == 1 ? "1 sold" : $"{near.Volume} sold", 10.5, BrushCache.FromHex("8fa0b6")),
        };
        var tw = lines.Max(l => l.Width) + 18;
        var th = lines.Sum(l => l.Height + 1) + 10;
        var tx = anchor.X + 12;
        if (tx + tw > w - 2) tx = anchor.X - 12 - tw;
        var tyy = Math.Clamp(anchor.Y - th / 2, 1, h - th - 1);
        dc.DrawRectangle(BrushCache.FromHex("0f1826"), TipPen, new Rect(tx, tyy, tw, th));
        var y = tyy + 5;
        foreach (var l in lines)
        {
            dc.DrawText(l, new Point(tx + 9, y));
            y += l.Height + 1;
        }
    }
}
