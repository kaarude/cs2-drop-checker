using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cs2DropChecker.App.Controls;

[Flags]
public enum CutCorners
{
    None = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomRight = 4,
    BottomLeft = 8,
}

public enum CutShape { Corners, TagRight, Parallelogram }

/// <summary>Border with the HUD's clipped corners. The border is drawn as an outer polygon behind an inset fill.</summary>
public sealed class CutBorder : Decorator
{
    private const FrameworkPropertyMetadataOptions Render = FrameworkPropertyMetadataOptions.AffectsRender;
    private const FrameworkPropertyMetadataOptions Relayout = FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender;

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(CutBorder), new FrameworkPropertyMetadata(null, Render));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(CutBorder), new FrameworkPropertyMetadata(null, Render));

    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(double), typeof(CutBorder),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | Relayout));

    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(
        nameof(Cut), typeof(double), typeof(CutBorder), new FrameworkPropertyMetadata(12.0, Relayout));

    public static readonly DependencyProperty CornersProperty = DependencyProperty.Register(
        nameof(Corners), typeof(CutCorners), typeof(CutBorder), new FrameworkPropertyMetadata(CutCorners.TopRight, Relayout));

    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(CutShape), typeof(CutBorder), new FrameworkPropertyMetadata(CutShape.Corners, Relayout));

    public static readonly DependencyProperty SlantProperty = DependencyProperty.Register(
        nameof(Slant), typeof(double), typeof(CutBorder), new FrameworkPropertyMetadata(8.0, Relayout));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(CutBorder),
        new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsMeasure | Relayout));

    public static readonly DependencyProperty ClipContentProperty = DependencyProperty.Register(
        nameof(ClipContent), typeof(bool), typeof(CutBorder), new FrameworkPropertyMetadata(true, Relayout));

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public double BorderThickness { get => (double)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public double Cut { get => (double)GetValue(CutProperty); set => SetValue(CutProperty, value); }
    public CutCorners Corners { get => (CutCorners)GetValue(CornersProperty); set => SetValue(CornersProperty, value); }
    public CutShape Shape { get => (CutShape)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public double Slant { get => (double)GetValue(SlantProperty); set => SetValue(SlantProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public bool ClipContent { get => (bool)GetValue(ClipContentProperty); set => SetValue(ClipContentProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        var b = BorderThickness;
        var p = Padding;
        var extraW = 2 * b + p.Left + p.Right;
        var extraH = 2 * b + p.Top + p.Bottom;
        if (Child is null) return new Size(extraW, extraH);
        Child.Measure(new Size(Math.Max(0, constraint.Width - extraW), Math.Max(0, constraint.Height - extraH)));
        return new Size(Child.DesiredSize.Width + extraW, Child.DesiredSize.Height + extraH);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var b = BorderThickness;
        var p = Padding;
        if (Child is not null)
        {
            var rect = new Rect(b + p.Left, b + p.Top,
                Math.Max(0, arrangeSize.Width - 2 * b - p.Left - p.Right),
                Math.Max(0, arrangeSize.Height - 2 * b - p.Top - p.Bottom));
            Child.Arrange(rect);
            if (ClipContent)
            {
                var inner = Build(Inset(new Rect(arrangeSize), b), Math.Max(0, Cut - b * 0.41), Math.Max(0, Slant - b * 0.41));
                var clip = inner.Clone();
                clip.Transform = new TranslateTransform(-rect.X, -rect.Y);
                clip.Freeze();
                Child.Clip = clip;
            }
            else
            {
                Child.Clip = null;
            }
        }
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Rect(RenderSize);
        var b = BorderThickness;
        if (b > 0 && BorderBrush is not null)
        {
            dc.DrawGeometry(BorderBrush, null, Build(size, Cut, Slant));
            if (Background is not null)
                dc.DrawGeometry(Background, null, Build(Inset(size, b), Math.Max(0, Cut - b * 0.41), Math.Max(0, Slant - b * 0.41)));
        }
        else if (Background is not null)
        {
            dc.DrawGeometry(Background, null, Build(size, Cut, Slant));
        }
    }

    private static Rect Inset(Rect r, double b) =>
        new(r.X + b, r.Y + b, Math.Max(0, r.Width - 2 * b), Math.Max(0, r.Height - 2 * b));

    private Geometry Build(Rect r, double c, double slant)
    {
        var points = new List<Point>(8);
        switch (Shape)
        {
            case CutShape.TagRight:
                points.Add(new Point(r.Left, r.Top));
                points.Add(new Point(r.Right, r.Top));
                points.Add(new Point(r.Right - slant, r.Bottom));
                points.Add(new Point(r.Left, r.Bottom));
                break;
            case CutShape.Parallelogram:
                points.Add(new Point(r.Left + slant, r.Top));
                points.Add(new Point(r.Right, r.Top));
                points.Add(new Point(r.Right - slant, r.Bottom));
                points.Add(new Point(r.Left, r.Bottom));
                break;
            default:
                c = Math.Min(c, Math.Min(r.Width, r.Height) / 2);
                var k = Corners;
                if (k.HasFlag(CutCorners.TopLeft)) { points.Add(new Point(r.Left, r.Top + c)); points.Add(new Point(r.Left + c, r.Top)); }
                else points.Add(new Point(r.Left, r.Top));
                if (k.HasFlag(CutCorners.TopRight)) { points.Add(new Point(r.Right - c, r.Top)); points.Add(new Point(r.Right, r.Top + c)); }
                else points.Add(new Point(r.Right, r.Top));
                if (k.HasFlag(CutCorners.BottomRight)) { points.Add(new Point(r.Right, r.Bottom - c)); points.Add(new Point(r.Right - c, r.Bottom)); }
                else points.Add(new Point(r.Right, r.Bottom));
                if (k.HasFlag(CutCorners.BottomLeft)) { points.Add(new Point(r.Left + c, r.Bottom)); points.Add(new Point(r.Left, r.Bottom - c)); }
                else points.Add(new Point(r.Left, r.Bottom));
                break;
        }

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            ctx.PolyLineTo(points.Skip(1).ToList(), false, false);
        }
        g.Freeze();
        return g;
    }
}
