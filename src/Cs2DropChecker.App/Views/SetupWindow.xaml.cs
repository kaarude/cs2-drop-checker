using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using Cs2DropChecker.App.ViewModels;

namespace Cs2DropChecker.App.Views;

public partial class SetupWindow : Window
{
    private SetupViewModel? _vm;

    public SetupWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => InputBox.Focus();
    }

    private void Attach()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as SetupViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        LoadAvatar();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SetupViewModel.Profile)) LoadAvatar();
    }

    private void LoadAvatar()
    {
        var url = _vm?.Profile?.AvatarUrl;
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
            bmp.DecodePixelWidth = 112;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            Avatar.Source = bmp;
        }
        catch (Exception)
        {
            Avatar.Source = null;
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
