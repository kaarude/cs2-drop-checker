using System.Net.Http;
using System.Windows.Threading;
using Cs2DropChecker.Core;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Settings;
using Cs2DropChecker.Core.Steam;
using Cs2DropChecker.Core.Storage;
using Cs2DropChecker.Core.Util;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Services;

public sealed record SettingsChangedEventArgs(AppSettings Old, AppSettings New);

/// <summary>Composition root. One instance for the lifetime of the process.</summary>
public sealed class AppServices : IDisposable
{
    public AppServices(Dispatcher ui, ILoggerFactory loggers)
    {
        Ui = ui;
        Loggers = loggers;
        Log = loggers.CreateLogger("App");
        SettingsStore = new SettingsStore(AppPaths.SettingsFile);
        Settings = SettingsStore.Load();
        Db = new AppDatabase(AppPaths.DatabaseFile);
        Http = HttpFactory.Create();
        Profiles = new SteamProfileClient(Http);
        Fetcher = new InventoryFetcher(loggers.CreateLogger<InventoryFetcher>());
        HttpTransport = new HttpInventoryTransport(Http);
        var fx = new FxService(Http, AppPaths.CacheDir);
        Prices = new PriceService(
            new SteamLivePriceSource(Http, Db, loggers.CreateLogger("SteamLive")),
            new CsgoTraderSteamSource(Http, fx, AppPaths.CacheDir),
            new SkinportSource(Http, AppPaths.CacheDir),
            fx,
            () => Settings,
            loggers.CreateLogger("Prices"));
        Images = new ImageCache(Http, AppPaths.ImagesDir, loggers.CreateLogger("Images"));
        ImageCache.Instance = Images;
        Cs2 = new Cs2Monitor();
        Session = new SteamSession(ui, Profiles, loggers.CreateLogger("Session"));
        WebTransport = new WebViewInventoryTransport(Session);
        Watcher = new WatcherService(this, loggers.CreateLogger("Watcher"));
        Market = new MarketService(this, loggers.CreateLogger("Market"));
        Updates = new UpdateService(this, loggers.CreateLogger("Updates"));
        Hotkeys = new HotkeyService(ui, loggers.CreateLogger("Hotkeys"));
    }

    public Dispatcher Ui { get; }
    public ILoggerFactory Loggers { get; }
    public ILogger Log { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; private set; }
    public AppDatabase Db { get; }
    public HttpClient Http { get; }
    public SteamProfileClient Profiles { get; }
    public InventoryFetcher Fetcher { get; }
    public HttpInventoryTransport HttpTransport { get; }
    public WebViewInventoryTransport WebTransport { get; }
    public PriceService Prices { get; }
    public ImageCache Images { get; }
    public Cs2Monitor Cs2 { get; }
    public SteamSession Session { get; }
    public WatcherService Watcher { get; }
    public MarketService Market { get; }
    public UpdateService Updates { get; }
    public HotkeyService Hotkeys { get; }
    public Overlay.OverlayService? Overlay { get; set; }

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    public void UpdateSettings(AppSettings next)
    {
        var old = Settings;
        SettingsStore.Save(next);
        Settings = next;
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(old, next));
    }

    /// <summary>Applies a small change to a copy of the settings and saves it.</summary>
    public void Mutate(Action<AppSettings> change)
    {
        var copy = Settings.Clone();
        change(copy);
        UpdateSettings(copy);
    }

    public void Dispose()
    {
        Watcher.Dispose();
        Updates.Dispose();
        Hotkeys.Dispose();
        Cs2.Dispose();
    }
}
