using System.Windows;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.App.ViewModels;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;

namespace Cs2DropChecker.App.Views;

public partial class SellWindow : Window
{
    private static SellWindow? _open;
    private readonly SellViewModel _vm;

    private SellWindow(SellViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.CloseRequested += (_, _) => Close();
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    /// <summary>Opens the sell dialog for one item. Only one is open at a time.</summary>
    public static void Open(AppServices s, InventoryItem item, PriceQuote? quote)
    {
        if (_open is { } current)
        {
            if (current._vm.Item.AssetId == item.AssetId)
            {
                current.Activate();
                return;
            }
            current.Close();
        }
        var w = new SellWindow(new SellViewModel(s, item, quote));
        Dialogs.Place(w);
        _open = w;
        w.Closed += (_, _) =>
        {
            if (_open == w) _open = null;
        };
        w.Show();
        w.Activate();
    }

    private void OnCustomFocus(object sender, RoutedEventArgs e)
    {
        if (!_vm.Custom.IsSelected) _vm.SelectCommand.Execute(_vm.Custom);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _vm.Dispose();
    }
}

public static class Dialogs
{
    /// <summary>Centres a dialog on the visible main window, or on screen when the app runs from the tray.</summary>
    public static void Place(Window w)
    {
        var owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(x => x.IsVisible && x.WindowState != WindowState.Minimized);
        if (owner is not null) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }
}
