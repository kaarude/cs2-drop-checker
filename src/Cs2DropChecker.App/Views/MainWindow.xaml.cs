using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cs2DropChecker.App.ViewModels;

namespace Cs2DropChecker.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateFrame();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void UpdateFrame()
    {
        if (WindowState == WindowState.Maximized)
        {
            var t = SystemParameters.WindowResizeBorderThickness;
            Root.Margin = new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4);
            Root.BorderThickness = new Thickness(0);
            MaxIcon.Data = Geometry.Parse("M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z");
        }
        else
        {
            Root.Margin = new Thickness(0);
            Root.BorderThickness = new Thickness(1);
            MaxIcon.Data = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is { } vm && vm.Page == Page.Settings && vm.Settings.HandleKey(e)) e.Handled = true;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        var app = App.Current;
        if (app.IsExiting) return;
        Vm?.Settings.CancelRecording();
        if (app.Services.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(app.ExitApp);
        }
    }
}
