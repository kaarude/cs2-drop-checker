using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Cs2DropChecker.App.Controls;

public enum TextAlign { Left, Center, Right }

/// <summary>
/// Single-line text with letter spacing, which TextBlock cannot do. Characters are drawn one by one with
/// FormattedText because GlyphRun ignores the weight instances of variable fonts such as Bahnschrift.
/// </summary>
public sealed class TrackedText : FrameworkElement
{
    private const FrameworkPropertyMetadataOptions Layout =
        FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TrackedText), new FrameworkPropertyMetadata("", Layout));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(TrackedText), new FrameworkPropertyMetadata(1.2, Layout));

    public static readonly DependencyProperty UpperProperty = DependencyProperty.Register(
        nameof(Upper), typeof(bool), typeof(TrackedText), new FrameworkPropertyMetadata(true, Layout));

    public static readonly DependencyProperty TrimProperty = DependencyProperty.Register(
        nameof(Trim), typeof(bool), typeof(TrackedText), new FrameworkPropertyMetadata(true, Layout));

    public static readonly DependencyProperty AlignProperty = DependencyProperty.Register(
        nameof(Align), typeof(TextAlign), typeof(TrackedText), new FrameworkPropertyMetadata(TextAlign.Left, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(TrackedText), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | Layout));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(TrackedText), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits | Layout));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
        typeof(TrackedText), new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | Layout));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(TrackedText), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | Layout));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public bool Upper { get => (bool)GetValue(UpperProperty); set => SetValue(UpperProperty, value); }
    public bool Trim { get => (bool)GetValue(TrimProperty); set => SetValue(TrimProperty, value); }
    public TextAlign Align { get => (TextAlign)GetValue(AlignProperty); set => SetValue(AlignProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private readonly record struct GlyphKey(string Ch, FontFamily Family, FontWeight Weight, double Size, Brush Brush, double Dip);

    private static readonly Dictionary<GlyphKey, FormattedText> Cache = new();
    private readonly List<(FormattedText Ft, double X)> _glyphs = new();
    private double _width;

    private static FormattedText Glyph(string ch, FontFamily family, FontWeight weight, double size, Brush brush, double dip)
    {
        var key = new GlyphKey(ch, family, weight, size, brush, dip);
        if (Cache.TryGetValue(key, out var ft)) return ft;
        if (Cache.Count > 60000) Cache.Clear();
        var tf = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        ft = new FormattedText(ch, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, brush, dip);
        Cache[key] = ft;
        return ft;
    }

    protected override Size MeasureOverride(Size available)
    {
        _glyphs.Clear();
        var text = Text ?? "";
        if (Upper) text = text.ToUpperInvariant();
        var family = FontFamily;
        var size = FontSize;
        var weight = FontWeight;
        var brush = Foreground ?? Brushes.White;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var spacing = Spacing;
        var height = Math.Ceiling(family.LineSpacing * size);

        var parts = new List<(FormattedText Ft, double W)>(text.Length);
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var ft = Glyph(e.GetTextElement(), family, weight, size, brush, dip);
            parts.Add((ft, ft.WidthIncludingTrailingWhitespace));
        }

        double total = 0;
        for (var i = 0; i < parts.Count; i++) total += parts[i].W + (i > 0 ? spacing : 0);

        var limit = available.Width;
        double x = 0;
        if (Trim && parts.Count > 0 && !double.IsInfinity(limit) && total > limit + 0.5)
        {
            var ellipsis = Glyph("…", family, weight, size, brush, dip);
            var ew = ellipsis.WidthIncludingTrailingWhitespace;
            foreach (var (ft, w) in parts)
            {
                if (x + w + spacing + ew > limit) break;
                _glyphs.Add((ft, x));
                x += w + spacing;
            }
            _glyphs.Add((ellipsis, x));
            _width = x + ew;
        }
        else
        {
            foreach (var (ft, w) in parts)
            {
                _glyphs.Add((ft, x));
                x += w + spacing;
            }
            _width = total;
        }

        var width = double.IsInfinity(limit) ? _width : Math.Min(_width, limit);
        return new Size(Math.Ceiling(width), height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var offset = Align switch
        {
            TextAlign.Center => (RenderSize.Width - _width) / 2,
            TextAlign.Right => RenderSize.Width - _width,
            _ => 0,
        };
        if (offset < 0) offset = 0;
        foreach (var (ft, x) in _glyphs) dc.DrawText(ft, new Point(offset + x, 0));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
    }
}
