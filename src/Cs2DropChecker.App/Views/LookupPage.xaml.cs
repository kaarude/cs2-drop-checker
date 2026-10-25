using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Cs2DropChecker.App.ViewModels;

namespace Cs2DropChecker.App.Views;

public partial class LookupPage : UserControl
{
    public LookupPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Query.Focus();
    }

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not LookupViewModel vm) return;
        e.Handled = true;
        if (vm.LookupCommand.CanExecute(null)) vm.LookupCommand.Execute(null);
    }
}

public sealed class Not : IValueConverter
{
    public static readonly Not Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
