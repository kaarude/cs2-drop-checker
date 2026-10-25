using System.Diagnostics;
using System.Windows;
using Cs2DropChecker.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Cs2DropChecker.App.Views;

/// <summary>Hosts Steam's login page in the shared WebView2 profile so the session can be used for inventory reads.</summary>
public partial class SignInWindow : Window
{
    private const string LoginUrl = "https://steamcommunity.com/login/home/?goto=";

    private readonly AppServices _s;
    private readonly ILogger _log;
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _checking;
    private bool _done;

    private SignInWindow(AppServices s)
    {
        _s = s;
        _log = s.Loggers.CreateLogger("SignIn");
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Shows the login window. Completes with true once Steam reports a signed-in account.</summary>
    public static Task<bool> RunAsync(AppServices s)
    {
        if (SteamSession.RuntimeVersion is null)
        {
            MessageBox.Show("Steam sign-in needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC.",
                "CS2 Drop Checker", MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.FromResult(false);
        }
        var w = new SignInWindow(s);
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive && x.IsVisible && x.ShowInTaskbar);
        if (owner is not null) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
        w.Activate();
        return w._result.Task;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var env = await _s.Session.GetEnvironmentAsync();
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.NewWindowRequested += (_, a) =>
            {
                a.Handled = true;
                if (IsSteam(a.Uri)) core.Navigate(a.Uri);
                else OpenExternal(a.Uri);
            };
            core.NavigationStarting += (_, a) =>
            {
                if (IsSteam(a.Uri)) return;
                a.Cancel = true;
                OpenExternal(a.Uri);
            };
            core.SourceChanged += (_, _) => ShowAddress(core.Source);
            core.NavigationCompleted += OnNavigationCompleted;
            core.Navigate(LoginUrl);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Sign-in window failed to start: {Message}", ex.Message);
            Loading.Visibility = Visibility.Collapsed;
            Failure.Visibility = Visibility.Visible;
            FailureText.Text = ex.Message;
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Loading.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        if (_done || _checking || !e.IsSuccess || Web.CoreWebView2 is not { } core) return;
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || !IsSteam(core.Source)) return;
        if (uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase)) return;

        _checking = true;
        StatusLine.Text = "Checking sign-in…";
        try
        {
            var state = await _s.Session.CheckSignedInAsync(CancellationToken.None);
            _log.LogInformation("Sign-in check after navigation: {State}", state);
            if (state == SignInState.SignedIn)
            {
                _done = true;
                _result.TrySetResult(true);
                Close();
                return;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Sign-in check failed: {Message}", ex.Message);
        }
        finally
        {
            _checking = false;
            if (!_done) StatusLine.Text = "";
        }
    }

    private void ShowAddress(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        Address.Text = uri.Host + uri.AbsolutePath.TrimEnd('/');
        Lock.Visibility = uri.Scheme == Uri.UriSchemeHttps ? Visibility.Visible : Visibility.Hidden;
    }

    private static bool IsSteam(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == "about") return true;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var h = uri.Host;
        return h is "steamcommunity.com" or "steampowered.com"
            || h.EndsWith(".steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".steampowered.com", StringComparison.OrdinalIgnoreCase);
    }

    private void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { _log.LogWarning("Could not open link: {Message}", ex.Message); }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try { Web.Dispose(); } catch (Exception) { }
        if (_done) return;
        _ = CheckOnCloseAsync();
    }

    private async Task CheckOnCloseAsync()
    {
        try
        {
            var check = _s.Session.CheckSignedInAsync(CancellationToken.None);
            var first = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(15)));
            _result.TrySetResult(first == check && await check == SignInState.SignedIn);
        }
        catch (Exception)
        {
            _result.TrySetResult(false);
        }
    }
}
