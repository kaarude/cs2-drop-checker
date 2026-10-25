using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Cs2DropChecker.App.Controls;

public static class BrushCache
{
    private static readonly Dictionary<string, SolidColorBrush> Solid = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Brush> Tints = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static Color ColorFromHex(string? hex, byte alpha = 255)
    {
        var h = (hex ?? "").TrimStart('#');
        if (h.Length != 6 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) v = 0xb0c3d9;
        return Color.FromArgb(alpha, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static SolidColorBrush FromHex(string? hex)
    {
        var key = hex ?? "";
        lock (Gate)
        {
            if (Solid.TryGetValue(key, out var b)) return b;
            b = new SolidColorBrush(ColorFromHex(hex));
            b.Freeze();
            Solid[key] = b;
            return b;
        }
    }

    public static SolidColorBrush FromArgb(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Radial glow from the top centre in the rarity colour, as on the in-game reveal.</summary>
    public static Brush Tint(string? hex, byte alpha = 0x7a)
    {
        var key = (hex ?? "") + "/" + alpha;
        lock (Gate)
        {
            if (Tints.TryGetValue(key, out var b)) return b;
            var c = ColorFromHex(hex, alpha);
            var rg = new RadialGradientBrush
            {
                Center = new Point(0.5, 0),
                GradientOrigin = new Point(0.5, 0),
                RadiusX = 0.85,
                RadiusY = 0.62,
            };
            rg.GradientStops.Add(new GradientStop(c, 0));
            rg.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            rg.Freeze();
            Tints[key] = rg;
            return rg;
        }
    }
}

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => BrushCache.FromHex(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HexTintConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BrushCache.Tint(value as string, parameter is string p && byte.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ? a : (byte)0x7a);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the value is truthy: true, non-empty string, non-zero number, non-null object. "!" parameter inverts.</summary>
public sealed class VisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            decimal m => m != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "!") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when value.ToString() equals the parameter (comma-separated list allowed).</summary>
public sealed class EqVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var v = value?.ToString() ?? "";
        var options = (parameter as string ?? "").Split(',', StringSplitOptions.TrimEntries);
        return options.Contains(v) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Two-way bool for radio buttons bound to an enum or string value.</summary>
public sealed class EqBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string p) return Binding.DoNothing;
        if (targetType.IsEnum) return Enum.Parse(targetType, p);
        if (targetType == typeof(int) || targetType == typeof(int?)) return int.Parse(p, CultureInfo.InvariantCulture);
        return p;
    }
}

public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? $"{i}%" : value is double d ? $"{Math.Round(d)}%" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
