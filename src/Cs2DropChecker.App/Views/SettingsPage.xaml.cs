using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cs2DropChecker.App.ViewModels;

namespace Cs2DropChecker.App.Views;

public partial class SettingsPage : UserControl
{
    private Point _dragStart;
    private SourceRow? _dragRow;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragRow = (sender as FrameworkElement)?.DataContext as SourceRow;
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow;
        _dragRow = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(SourceRow), row), DragDropEffects.Move);
    }

    private void OnSourceDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SourceRow)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSourceDrop(object sender, DragEventArgs e)
    {
        if (Vm is not { } vm || e.Data.GetData(typeof(SourceRow)) is not SourceRow dragged) return;
        if ((sender as FrameworkElement)?.DataContext is not SourceRow target || target == dragged) return;
        vm.MoveSource(dragged, vm.Sources.IndexOf(target));
        e.Handled = true;
    }
}
