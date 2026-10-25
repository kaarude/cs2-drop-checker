using System.Text.Json;
using System.Text.Json.Serialization;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Util;

namespace Cs2DropChecker.Core.Settings;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool FirstRunDone { get; set; }
    public string SteamId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public bool UseSignIn { get; set; }
    public bool WatchOnlyWhileCs2Running { get; set; } = true;
    public int PollIntervalMinutes { get; set; } = 3;
    public string Currency { get; set; } = "EUR";
    public List<PriceSourceSetting> PriceSources { get; set; } = DefaultSources();
    public OverlaySettings Overlay { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public bool StartWithWindows { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public DateTimeOffset? LastViewedDropsAt { get; set; }
    public ProfitSettings Profit { get; set; } = new();
    public ShareSettings Share { get; set; } = new();
    public FriendsSettings Friends { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public WeeklyReminderSettings WeeklyReminder { get; set; } = new();

    [JsonIgnore] public ulong SteamId64 => ulong.TryParse(SteamId, out var v) ? v : 0;

    public static List<PriceSourceSetting> DefaultSources() =>
        PriceSourceIds.All.Select(id => new PriceSourceSetting { Id = id, Enabled = true }).ToList();

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;
}

public sealed class PriceSourceSetting
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public enum OverlayPosition { TopLeft, TopCenter, TopRight, MiddleLeft, Center, MiddleRight, BottomLeft, BottomCenter, BottomRight }

public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public OverlayPosition Position { get; set; } = OverlayPosition.TopRight;
    public int OffsetX { get; set; } = 16;
    public int OffsetY { get; set; } = 16;
    public int Monitor { get; set; }
    public int SizePercent { get; set; } = 80;
    public int OpacityPercent { get; set; } = 96;
    public int SecondsPerCard { get; set; } = 10;
    public bool QueueWhileOff { get; set; } = true;
}

public sealed class HotkeySettings
{
    public string ToggleOverlay { get; set; } = "Ctrl+Shift+O";
    public string ShowTotalValue { get; set; } = "Ctrl+Shift+V";
    public string RefreshNow { get; set; } = "Ctrl+Shift+R";
    public string ShareLastDrop { get; set; } = "Ctrl+Shift+S";
}

public sealed class ProfitSettings
{
    /// <summary>Price of one case key. Null uses Valve's store price converted to the chosen currency.</summary>
    public decimal? KeyPrice { get; set; }
    public string? KeyPriceCurrency { get; set; }
}

public sealed class ShareSettings
{
    /// <summary>Where share cards are saved. Empty means Pictures\CS2 Drop Checker.</summary>
    public string Folder { get; set; } = "";
    public bool ShowProfile { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
}

public sealed class FriendsSettings
{
    public int RefreshMinutes { get; set; } = 30;
}

public sealed class AlertSettings
{
    public bool Toasts { get; set; } = true;
    /// <summary>Optional Discord webhook. Kept only in the local settings file and never written to logs.</summary>
    public string DiscordWebhookUrl { get; set; } = "";
}

public sealed class WeeklyReminderSettings
{
    public bool Enabled { get; set; } = true;
    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;
    /// <summary>Local time of day, HH:mm.</summary>
    public string Time { get; set; } = "19:00";
    public bool ShowOverlayCard { get; set; } = true;
    /// <summary>Start of the CS2 week the reminder was last shown for.</summary>
    public DateTimeOffset? LastShownWeek { get; set; }

    [JsonIgnore]
    public TimeSpan TimeOfDay => TimeSpan.TryParseExact(Time, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(19, 0, 0);
}

public sealed class SettingsStore(string path)
{
    public string Path => path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonDefaults.Options) ?? new AppSettings();
            Normalize(s);
            return s;
        }
        catch (Exception)
        {
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception) { }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonDefaults.Indented));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Normalize(AppSettings s)
    {
        s.PollIntervalMinutes = Math.Clamp(s.PollIntervalMinutes, 2, 60);
        if (!Currencies.IsKnown(s.Currency)) s.Currency = "EUR";
        var known = s.PriceSources.Where(p => PriceSourceIds.All.Contains(p.Id)).GroupBy(p => p.Id).Select(g => g.First()).ToList();
        foreach (var id in PriceSourceIds.All)
            if (known.All(p => p.Id != id)) known.Add(new PriceSourceSetting { Id = id, Enabled = true });
        s.PriceSources = known;
        s.Overlay ??= new OverlaySettings();
        s.Overlay.SizePercent = Math.Clamp(s.Overlay.SizePercent, 50, 160);
        s.Overlay.OpacityPercent = Math.Clamp(s.Overlay.OpacityPercent, 40, 100);
        s.Overlay.SecondsPerCard = Math.Clamp(s.Overlay.SecondsPerCard, 3, 60);
        s.Overlay.OffsetX = Math.Clamp(s.Overlay.OffsetX, 0, 2000);
        s.Overlay.OffsetY = Math.Clamp(s.Overlay.OffsetY, 0, 2000);
        s.Hotkeys ??= new HotkeySettings();
        s.Hotkeys.ShareLastDrop ??= "Ctrl+Shift+S";
        s.Profit ??= new ProfitSettings();
        if (s.Profit.KeyPrice is { } k && (k < 0 || k > 1000)) s.Profit.KeyPrice = null;
        if (s.Profit.KeyPrice is not null && !Currencies.IsKnown(s.Profit.KeyPriceCurrency)) s.Profit.KeyPriceCurrency = s.Currency;
        s.Share ??= new ShareSettings();
        s.Share.Folder = (s.Share.Folder ?? "").Trim();
        s.Friends ??= new FriendsSettings();
        s.Friends.RefreshMinutes = Math.Clamp(s.Friends.RefreshMinutes, 15, 240);
        s.Alerts ??= new AlertSettings();
        s.Alerts.DiscordWebhookUrl = (s.Alerts.DiscordWebhookUrl ?? "").Trim();
        s.WeeklyReminder ??= new WeeklyReminderSettings();
        s.WeeklyReminder.Time = s.WeeklyReminder.TimeOfDay.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
    }
}
