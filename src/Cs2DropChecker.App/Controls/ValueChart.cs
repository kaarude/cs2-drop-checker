using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Storage;

namespace Cs2DropChecker.App.Controls;

public sealed record ChartMarker(DateTimeOffset At, string Title, string Detail, bool Big);

/// <summary>Inventory value line with markers for new items, drawn directly for speed.</summary>
public sealed class ValueChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<ValuePoint>), typeof(ValueChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<ChartMarker>), typeof(ValueChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrencyProperty = DependencyProperty.Register(
        nameof(Currency), typeof(string), typeof(ValueChart),
        new FrameworkPropertyMetadata("EUR", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(DateTimeOffset?), typeof(ValueChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ValuePoint>? Points { get => (IReadOnlyList<ValuePoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IReadOnlyList<ChartMarker>? Markers { get => (IReadOnlyList<ChartMarker>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public string Currency { get => (string)GetValue(CurrencyProperty); set => SetValue(CurrencyProperty, value); }
    public DateTimeOffset? From { get => (DateTimeOffset?)GetValue(FromProperty); set => SetValue(FromProperty, value); }

    private static readonly Brush AxisBrush = BrushCache.FromHex("7f8ea3");
    private static readonly Pen GridPen = Frozen(new Pen(BrushCache.FromArgb(0x2e, 0x5d, 0x79, 0xae), 1));
    private static readonly Pen LinePen = Frozen(new Pen(BrushCache.FromHex("de9b35"), 2) { LineJoin = PenLineJoin.Round });
    private static readonly Pen MarkerPen = Frozen(new Pen(BrushCache.FromHex("7fa0e0"), 2));
    private static readonly Pen CrossPen = Frozen(new Pen(BrushCache.FromArgb(0x66, 0xde, 0x9b, 0x35), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen TipPen = Frozen(new Pen(BrushCache.FromHex("de9b35"), 1));
    private static readonly Brush Fill = FrozenBrush(new LinearGradientBrush(
        Color.FromArgb(0x40, 0xde, 0x9b, 0x35), Color.FromArgb(0x00, 0xde, 0x9b, 0x35), 90));
    private static readonly Typeface Face = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private Point? _mouse;

    private static Pen Frozen(Pen p) { p.Freeze(); return p; }
    private static Brush FrozenBrush(Brush b) { b.Freeze(); return b; }

    public ValueChart()
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

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        const double left = 54, right = 18, top = 14, bottom = 26;
        var plot = new Rect(left, top, Math.Max(10, w - left - right), Math.Max(10, h - top - bottom));
        var all = Points ?? [];
        var currency = Currency;

        if (all.Count == 0)
        {
            for (var i = 0; i <= 4; i++)
            {
                var gy = plot.Bottom - plot.Height * i / 4;
                dc.DrawLine(GridPen, new Point(plot.Left, gy), new Point(plot.Right, gy));
            }
            var msg = Txt("VALUE HISTORY STARTS WITH THE FIRST INVENTORY CHECK", 11, AxisBrush);
            dc.DrawText(msg, new Point(plot.Left + (plot.Width - msg.Width) / 2, plot.Top + (plot.Height - msg.Height) / 2));
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var t1 = all[^1].At > now ? all[^1].At : now;
        var t0 = From is { } f && f > all[0].At ? f : all[0].At;
        if (t1 - t0 < TimeSpan.FromHours(1)) t0 = t1 - TimeSpan.FromHours(1);

        var pts = new List<ValuePoint>();
        var before = all.LastOrDefault(p => p.At < t0);
        if (before is not null) pts.Add(before with { At = t0 });
        pts.AddRange(all.Where(p => p.At >= t0));
        if (pts.Count == 0) pts.Add(all[^1] with { At = t0 });

        var maxV = (double)pts.Max(p => p.Total);
        var top0 = Nice(Math.Max(1, maxV * 1.12));
        double X(DateTimeOffset t) => plot.Left + plot.Width * ((t - t0).TotalSeconds / Math.Max(1, (t1 - t0).TotalSeconds));
        double Y(decimal v) => plot.Bottom - plot.Height * ((double)v / top0);

        for (var i = 0; i <= 4; i++)
        {
            var value = top0 * i / 4;
            var gy = plot.Bottom - plot.Height * i / 4;
            dc.DrawLine(GridPen, new Point(plot.Left, gy), new Point(plot.Right, gy));
            var label = Txt(Money.Short((decimal)value, currency), 11, AxisBrush);
            dc.DrawText(label, new Point(plot.Left - 10 - label.Width, gy - label.Height / 2));
        }

        var span = t1 - t0;
        var ticks = Math.Clamp((int)(plot.Width / 150), 2, 7);
        for (var i = 0; i <= ticks; i++)
        {
            var t = t0 + TimeSpan.FromTicks(span.Ticks * i / ticks);
            var local = t.ToLocalTime();
            var s = span <= TimeSpan.FromDays(2) ? local.ToString("HH:mm", CultureInfo.InvariantCulture) : local.ToString("MMM d", CultureInfo.InvariantCulture);
            var label = Txt(s, 11, AxisBrush);
            var lx = Math.Clamp(X(t) - label.Width / 2, plot.Left - 4, plot.Right - label.Width + 4);
            dc.DrawText(label, new Point(lx, plot.Bottom + 8));
        }

        var line = new List<Point>(pts.Count * 2 + 1);
        for (var i = 0; i < pts.Count; i++)
        {
            var p = new Point(X(pts[i].At), Y(pts[i].Total));
            if (i > 0) line.Add(new Point(p.X, line[^1].Y));
            line.Add(p);
        }
        var end = new Point(plot.Right, line[^1].Y);
        line.Add(end);

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(line[0].X, plot.Bottom), true, true);
            ctx.PolyLineTo(line, false, false);
            ctx.LineTo(new Point(end.X, plot.Bottom), false, false);
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

        decimal ValueAt(DateTimeOffset t)
        {
            var v = pts[0].Total;
            foreach (var p in pts)
            {
                if (p.At > t) break;
                v = p.Total;
            }
            return v;
        }

        var markerPoints = new List<(Point P, ChartMarker M)>();
        foreach (var m in Markers ?? [])
        {
            if (m.At < t0 || m.At > t1) continue;
            var mp = new Point(X(m.At), Y(ValueAt(m.At)));
            markerPoints.Add((mp, m));
            if (m.Big) dc.DrawEllipse(BrushCache.FromHex("f3b350"), null, mp, 5.5, 5.5);
            else dc.DrawEllipse(BrushCache.FromHex("0b1119"), MarkerPen, mp, 4.2, 4.2);
        }
        dc.DrawEllipse(BrushCache.FromHex("f3b350"), null, new Point(X(pts[^1].At), Y(pts[^1].Total)), 5, 5);

        if (_mouse is not { } mouse || !plot.Contains(new Point(Math.Clamp(mouse.X, plot.Left, plot.Right), mouse.Y))) return;
        var near = markerPoints.Where(x => Math.Abs(x.P.X - mouse.X) <= 12).OrderBy(x => Math.Abs(x.P.X - mouse.X)).ToList();
        DateTimeOffset at;
        Point anchor;
        if (near.Count > 0)
        {
            at = near[0].M.At;
            anchor = near[0].P;
        }
        else
        {
            var mx = Math.Clamp(mouse.X, plot.Left, plot.Right);
            at = t0 + TimeSpan.FromSeconds((mx - plot.Left) / plot.Width * span.TotalSeconds);
            anchor = new Point(mx, Y(ValueAt(at)));
        }
        dc.DrawLine(CrossPen, new Point(anchor.X, plot.Top), new Point(anchor.X, plot.Bottom));

        var lines = new List<FormattedText>
        {
            Txt($"{at.ToLocalTime().ToString(span <= TimeSpan.FromDays(2) ? "HH:mm" : "MMM d, HH:mm", CultureInfo.InvariantCulture)} · {Money.Format(ValueAt(at), currency)}", 12, BrushCache.FromHex("e6ebf1"), true),
        };
        foreach (var (_, m) in near.Where(x => Math.Abs(x.P.X - near[0].P.X) < 6).Take(4))
        {
            lines.Add(Txt(m.Title, 11.5, BrushCache.FromHex("f3b350"), true));
            if (m.Detail.Length > 0) lines.Add(Txt(m.Detail, 11, BrushCache.FromHex("8fa0b6")));
        }
        var tw = lines.Max(l => l.Width) + 20;
        var th = lines.Sum(l => l.Height + 2) + 12;
        var tx = anchor.X + 14;
        if (tx + tw > w - 4) tx = anchor.X - 14 - tw;
        var ty = Math.Clamp(anchor.Y - th - 10, 2, h - th - 2);
        dc.DrawRectangle(BrushCache.FromHex("0f1826"), TipPen, new Rect(tx, ty, tw, th));
        var y = ty + 6;
        foreach (var l in lines)
        {
            dc.DrawText(l, new Point(tx + 10, y));
            y += l.Height + 2;
        }
    }

    private static double Nice(double v)
    {
        var exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var f = v / exp;
        var nf = f <= 1 ? 1 : f <= 1.2 ? 1.2 : f <= 1.6 ? 1.6 : f <= 2 ? 2 : f <= 2.4 ? 2.4 : f <= 3 ? 3 : f <= 4 ? 4 : f <= 5 ? 5 : f <= 6 ? 6 : f <= 8 ? 8 : 10;
        return nf * exp;
    }
}
