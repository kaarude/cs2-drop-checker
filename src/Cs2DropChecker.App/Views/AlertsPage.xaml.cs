using System.Windows.Controls;
using Cs2DropChecker.App.ViewModels;

namespace Cs2DropChecker.App.Views;

public partial class AlertsPage : UserControl
{
    public AlertsPage()
    {
        InitializeComponent();
    }

    // Only real picks reach the view model. Selections that drop to nothing (the list reloading, or the page being
    // swapped out) are ignored instead of clearing the form.
    private void OnKindPicked(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is AlertKindOption kind && DataContext is AlertsViewModel vm) vm.Kind = kind;
    }

    private void OnItemPicked(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is Option item && DataContext is AlertsViewModel vm) vm.Item = item;
    }
}
