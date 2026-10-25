using System.Windows;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.App.ViewModels;
using Cs2DropChecker.Core.Models;

namespace Cs2DropChecker.App.Views;

public partial class BulkSellWindow : Window
{
    private static BulkSellWindow? _open;
    private readonly BulkSellViewModel _vm;

    private BulkSellWindow(BulkSellViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.CloseRequested += (_, _) => Close();
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    /// <summary>Opens the bulk sell dialog. A second call while one is open just brings it forward.</summary>
    public static void Open(AppServices s, IReadOnlyList<InventoryItem> items, Action? onListed)
    {
        if (items.Count == 0) return;
        if (_open is { } current)
        {
            current.Activate();
            return;
        }
        var w = new BulkSellWindow(new BulkSellViewModel(s, items, onListed));
        Dialogs.Place(w);
        _open = w;
        w.Closed += (_, _) =>
        {
            if (_open == w) _open = null;
        };
        w.Show();
        w.Activate();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.IsSelling)
        {
            // Closing mid-run would leave the remaining requests in flight with no visible status; stop after the current one instead.
            _vm.StopCommand.Execute(null);
            e.Cancel = true;
            _vm.Error = "Stopping after the current listing…";
            _closeWhenDone = true;
            return;
        }
        base.OnClosing(e);
    }

    private bool _closeWhenDone;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _vm.Dispose();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _vm.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(BulkSellViewModel.IsSelling) && !_vm.IsSelling && _closeWhenDone) Dispatcher.BeginInvoke(Close);
        };
    }
}
