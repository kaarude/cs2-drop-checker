using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Cs2DropChecker.App.ViewModels;

namespace Cs2DropChecker.App.Views;

/// <summary>Profile header plus tile grid, shared by the Inventory and Lookup pages.</summary>
public partial class ProfileInventoryView : UserControl
{
    private const double TileStride = 128;
    private ProfileInventoryViewModel? _vm;

    public ProfileInventoryView()
    {
        InitializeComponent();
        Tiles.SizeChanged += (_, e) => UpdateColumns(e.NewSize.Width);
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) =>
        {
            Attach();
            UpdateColumns(Tiles.ActualWidth);
        };
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        Detach();
        _vm = DataContext as ProfileInventoryViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmChanged;
        LoadAvatar();
    }

    private void Detach()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = null;
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileInventoryViewModel.AvatarUrl)) LoadAvatar();
    }

    private void LoadAvatar()
    {
        var url = _vm?.AvatarUrl;
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Avatar.Source = null;
            return;
        }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.DecodePixelWidth = 116;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            Avatar.Source = bmp;
        }
        catch (Exception)
        {
            Avatar.Source = null;
        }
    }

    private void UpdateColumns(double width)
    {
        if (_vm is null || width <= 0) return;
        _vm.Grid.SetColumns((int)Math.Max(1, Math.Floor((width - 12) / TileStride)));
    }
}
