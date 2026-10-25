using System.Windows;
using System.Windows.Media;
using Cs2DropChecker.Core.Valuation;

namespace Cs2DropChecker.App.Controls;

/// <summary>Proportional colour bar for the six value groups.</summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable<ValueGroup>), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(SegmentBar), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<ValueGroup>? Segments { get => (IEnumerable<ValueGroup>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        var list = Segments?.Where(s => s.Value > 0).ToList() ?? [];
        var total = list.Sum(s => s.Value);
        if (total <= 0 || w <= 0)
        {
            dc.DrawRectangle(BrushCache.FromHex("1a2433"), null, new Rect(0, 0, w, h));
            return;
        }
        var gap = Gap;
        var usable = Math.Max(0, w - gap * (list.Count - 1));
        double x = 0;
        foreach (var s in list)
        {
            var sw = Math.Max(1.5, usable * (double)(s.Value / total));
            dc.DrawRectangle(BrushCache.FromHex(s.ColorHex), null, new Rect(x, 0, Math.Min(sw, w - x), h));
            x += sw + gap;
            if (x >= w) break;
        }
    }
}
