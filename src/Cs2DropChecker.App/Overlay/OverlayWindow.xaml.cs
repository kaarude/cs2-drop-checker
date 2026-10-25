using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.Core.Settings;

namespace Cs2DropChecker.App.Overlay;

/// <summary>Transparent, click-through, topmost window that never takes focus from the game.</summary>
public partial class OverlayWindow : Window
{
    private const double BaseWidth = 250;
    private const double BaseHeight = 640;
    private OverlaySettings? _layout;
    private bool _placing;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.MakeClickThrough(new WindowInteropHelper(this).Handle);
    }

    public void ApplyLayout(OverlaySettings o)
    {
        _layout = o;
        var scale = o.SizePercent / 100.0;
        Opacity = o.OpacityPercent / 100.0;
        Scaler.LayoutTransform = new ScaleTransform(scale, scale);
        var (h, v) = Align(o.Position);
        Scaler.HorizontalAlignment = HorizontalAlignment.Left;
        Stack.HorizontalAlignment = h;
        Stack.VerticalAlignment = v;
        foreach (UIElement child in Stack.Children)
            if (child is FrameworkElement fe) fe.HorizontalAlignment = h;
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;
        Place();
    }

    private static (HorizontalAlignment, VerticalAlignment) Align(OverlayPosition p) => p switch
    {
        OverlayPosition.TopLeft => (HorizontalAlignment.Left, VerticalAlignment.Top),
        OverlayPosition.TopCenter => (HorizontalAlignment.Center, VerticalAlignment.Top),
        OverlayPosition.TopRight => (HorizontalAlignment.Right, VerticalAlignment.Top),
        OverlayPosition.MiddleLeft => (HorizontalAlignment.Left, VerticalAlignment.Center),
        OverlayPosition.Center => (HorizontalAlignment.Center, VerticalAlignment.Center),
        OverlayPosition.MiddleRight => (HorizontalAlignment.Right, VerticalAlignment.Center),
        OverlayPosition.BottomLeft => (HorizontalAlignment.Left, VerticalAlignment.Bottom),
        OverlayPosition.BottomCenter => (HorizontalAlignment.Center, VerticalAlignment.Bottom),
        _ => (HorizontalAlignment.Right, VerticalAlignment.Bottom),
    };

    /// <summary>Positions the window in physical pixels on the chosen monitor.</summary>
    private void Place()
    {
        var o = _layout;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (o is null || hwnd == IntPtr.Zero || _placing) return;
        var monitors = Native.GetMonitors();
        if (monitors.Count == 0) return;
        var m = o.Monitor >= 0 && o.Monitor < monitors.Count ? monitors[o.Monitor] : monitors[0];
        var b = m.Bounds;
        var k = m.Scale;
        var w = (int)Math.Ceiling(Width * k);
        var h = (int)Math.Ceiling(Height * k);
        var ox = (int)Math.Round(o.OffsetX * k);
        var oy = (int)Math.Round(o.OffsetY * k);
        var (ha, va) = Align(o.Position);
        var x = ha switch
        {
            HorizontalAlignment.Left => b.Left + ox,
            HorizontalAlignment.Center => b.Left + (b.Width - w) / 2,
            _ => b.Right - ox - w,
        };
        var y = va switch
        {
            VerticalAlignment.Top => b.Top + oy,
            VerticalAlignment.Center => b.Top + (b.Height - h) / 2,
            _ => b.Bottom - oy - h,
        };
        _placing = true;
        try { Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE); }
        finally { _placing = false; }
    }

    public void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(Place);
    }
}
