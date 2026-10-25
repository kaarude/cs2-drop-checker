using System.Windows;
using System.Windows.Controls;
using Cs2DropChecker.App.Services;

namespace Cs2DropChecker.App.Controls;

/// <summary>Attached properties that load Steam item icons through the shared image cache.</summary>
public static class Img
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Img), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(Img), new PropertyMetadata(200));

    public static string? GetIcon(DependencyObject d) => (string?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, string? value) => d.SetValue(IconProperty, value);
    public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject d, int value) => d.SetValue(DecodeWidthProperty, value);

    private static async void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        var icon = e.NewValue as string;
        var cache = ImageCache.Instance;
        if (string.IsNullOrEmpty(icon) || cache is null)
        {
            image.Source = null;
            return;
        }
        var width = GetDecodeWidth(image);
        var hit = cache.TryGetMemory(icon, width);
        image.Source = hit;
        if (hit is not null) return;
        try
        {
            var loaded = await cache.GetAsync(icon, width);
            if (GetIcon(image) == icon) image.Source = loaded;
        }
        catch (Exception)
        {
        }
    }
}
