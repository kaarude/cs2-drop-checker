using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cs2DropChecker.App.Overlay;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.App.ViewModels;
using Cs2DropChecker.App.Views;
using Cs2DropChecker.Core;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Cs2DropChecker.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private Serilog.Core.Logger? _serilog;
    private SerilogLoggerFactory? _loggers;
    private Microsoft.Extensions.Logging.ILogger? _log;
    private AppServices? _s;
    private MainViewModel? _vm;
    private MainWindow? _main;
    private SetupWindow? _setup;
    private TaskbarIcon? _tray;
    private MenuItem? _trayOverlay;
    private bool _exiting;

    public static new App Current => (App)Application.Current;
    public AppServices Services => _s ?? throw new InvalidOperationException("Not started");
    public bool IsExiting => _exiting;

    public static string Version =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var minimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        UpdateService.FinishPreviousUpdate(e.Args);

        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            _single.SignalFirst();
            _single.Dispose();
            Shutdown();
            return;
        }

        _serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppPaths.LogsDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5_000_000,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        _loggers = new SerilogLoggerFactory(_serilog);
        _log = _loggers.CreateLogger("App");
        _log.LogInformation("CS2 Drop Checker {Version} starting (minimized: {Min})", Version, minimized);

        DispatcherUnhandledException += (_, a) =>
        {
            _log.LogError(a.Exception, "Unhandled UI exception");
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => _log?.LogCritical(a.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            _log?.LogWarning(a.Exception, "Unobserved task exception");
            a.SetObserved();
        };

        _s = new AppServices(Dispatcher, _loggers);
        _s.Hotkeys.Initialize();
        _s.Hotkeys.Apply(_s.Settings.Hotkeys);
        _s.Hotkeys.Pressed += OnHotkey;
        _s.Overlay = new OverlayService(_s, _loggers.CreateLogger("Overlay"));
        _s.SettingsChanged += OnSettingsChanged;
        StartupRegistration.Apply(_s.Settings.StartWithWindows && _s.Settings.FirstRunDone);
        _s.Market.LoadSaved();
        _s.Updates.Start();

        _vm = new MainViewModel(_s);
        CreateTray();
        _s.Notifier.Tray = _tray;
        _s.Watcher.InventoryChanged += (_, _) => UpdateTrayTip();
        _single.ListenForShow(() => Dispatcher.BeginInvoke(ShowMain));

        _ = StartWatchingAsync();

        if (!_s.Settings.FirstRunDone || _s.Settings.SteamId64 == 0) ShowSetup();
        else if (!minimized) ShowMain();
    }

    private async Task StartWatchingAsync()
    {
        var s = Services;
        if (s.Settings.UseSignIn && SteamSession.RuntimeVersion is not null)
        {
            try
            {
                var check = s.Session.CheckSignedInAsync(CancellationToken.None);
                var done = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(25)));
                if (done == check) _log!.LogInformation("Steam sign-in on startup: {State}", await check);
                else _log!.LogInformation("Steam sign-in check timed out on startup");
            }
            catch (Exception ex)
            {
                _log!.LogWarning("Steam sign-in check failed: {Message}", ex.Message);
            }
        }
        s.Watcher.Start();
        s.Alerts.Start();
        s.Friends.Start();
        s.Weekly.Start();
    }

    private void CreateTray()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("HudMenu") };
        var open = new MenuItem { Header = "Open CS2 Drop Checker" };
        open.Click += (_, _) => ShowMain();
        _trayOverlay = new MenuItem();
        _trayOverlay.Click += (_, _) => Services.Overlay?.ToggleEnabled();
        var value = new MenuItem { Header = "Show total value" };
        value.Click += (_, _) => Services.Overlay?.ToggleValuePanel();
        var refresh = new MenuItem { Header = "Check inventory now" };
        refresh.Click += (_, _) => Services.Watcher.PollNow();
        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => ExitApp();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(_trayOverlay);
        menu.Items.Add(value);
        menu.Items.Add(refresh);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        menu.Opened += (_, _) => UpdateTrayMenu();
        foreach (var item in menu.Items.OfType<MenuItem>()) item.Style = (Style)FindResource("HudMenuItem");
        UpdateTrayMenu();

        _tray = new TaskbarIcon
        {
            ToolTipText = "CS2 Drop Checker",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseUp += (_, _) => ShowMain();
        _tray.ForceCreate(false);
    }

    private void UpdateTrayMenu()
    {
        if (_trayOverlay is null || _s is null) return;
        var h = _s.Settings.Hotkeys;
        _trayOverlay.Header = _s.Settings.Overlay.Enabled ? "Turn overlay off" : "Turn overlay on";
        _trayOverlay.InputGestureText = h.ToggleOverlay;
        if (_trayOverlay.Parent is ContextMenu m)
        {
            foreach (var item in m.Items.OfType<MenuItem>())
            {
                if ((string?)item.Header == "Show total value") item.InputGestureText = h.ShowTotalValue;
                if ((string?)item.Header == "Check inventory now") item.InputGestureText = h.RefreshNow;
            }
        }
    }

    private void UpdateTrayTip()
    {
        if (_tray is null || _s is null) return;
        var w = _s.Watcher;
        _tray.ToolTipText = w.Current is null ? "CS2 Drop Checker" : $"CS2 Drop Checker · {Money.Format(w.Value.Total, _s.Settings.Currency)}";
    }

    private void OnHotkey(HotkeyAction action)
    {
        var s = Services;
        _log!.LogInformation("Shortcut: {Action}", action);
        switch (action)
        {
            case HotkeyAction.ToggleOverlay:
                s.Overlay?.ToggleEnabled();
                break;
            case HotkeyAction.ShowTotalValue:
                s.Overlay?.ToggleValuePanel();
                break;
            case HotkeyAction.RefreshNow:
                s.Watcher.PollNow();
                s.Overlay?.ShowToast("Checking inventory", s.Settings.Hotkeys.RefreshNow, true);
                break;
            case HotkeyAction.ShareLastDrop:
                _ = ShareLastDropAsync();
                break;
        }
    }

    private async Task ShareLastDropAsync()
    {
        var s = Services;
        var r = await s.Share.ShareLastDropAsync();
        s.Overlay?.ShowToast(r.Ok ? (s.Settings.Share.CopyToClipboard ? "Share card copied" : "Share card saved") : r.Message, s.Settings.Hotkeys.ShareLastDrop, r.Ok);
        _vm?.ShowFlash(r.Message, r.Ok ? "ok" : "warn");
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        var s = Services;
        var o = e.Old;
        var n = e.New;
        if (o.SteamId != n.SteamId)
        {
            _log!.LogInformation("Watched account changed");
            s.Market.Reset();
            s.Watcher.Reset(accountChanged: true);
        }
        if (o.Currency != n.Currency)
        {
            s.Prices.Live.Warm(n.Currency);
            s.Watcher.Reset(accountChanged: false);
            s.Watcher.RefreshPricesNow();
        }
        var oldOrder = string.Join(",", o.PriceSources.Select(p => p.Id + p.Enabled));
        var newOrder = string.Join(",", n.PriceSources.Select(p => p.Id + p.Enabled));
        if (oldOrder != newOrder)
        {
            s.Watcher.Reset(accountChanged: false);
            s.Watcher.RefreshPricesNow();
        }
        if (!HotkeyService.Entries(o.Hotkeys).SequenceEqual(HotkeyService.Entries(n.Hotkeys)))
            s.Hotkeys.Apply(n.Hotkeys);
        if (o.StartWithWindows != n.StartWithWindows || o.FirstRunDone != n.FirstRunDone)
            StartupRegistration.Apply(n.StartWithWindows && n.FirstRunDone);
        UpdateTrayMenu();
        UpdateTrayTip();
    }

    private void ShowSetup()
    {
        if (_setup is not null)
        {
            _setup.Activate();
            return;
        }
        var vm = new SetupViewModel(Services);
        _setup = new SetupWindow { DataContext = vm };
        var finished = false;
        vm.Completed += (_, _) =>
        {
            finished = true;
            _setup?.Close();
            ShowMain();
        };
        _setup.Closed += (_, _) =>
        {
            _setup = null;
            if (!finished && !Services.Settings.FirstRunDone) ExitApp();
        };
        _setup.Show();
        _setup.Activate();
    }

    public void ShowMain()
    {
        if (_s is null || _exiting) return;
        if (_setup is not null)
        {
            _setup.Activate();
            return;
        }
        if (_main is null)
        {
            _main = new MainWindow { DataContext = _vm };
            _main.Closed += (_, _) => _main = null;
        }
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;
        _main.Topmost = false;
        _main.Focus();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _log?.LogInformation("Exiting");
        try { _tray?.Dispose(); } catch (Exception) { }
        try { _s?.Overlay?.Dispose(); } catch (Exception) { }
        try { _main?.Close(); } catch (Exception) { }
        try { _s?.Dispose(); } catch (Exception) { }
        _single?.Dispose();
        _loggers?.Dispose();
        _serilog?.Dispose();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        ExitApp();
    }
}
