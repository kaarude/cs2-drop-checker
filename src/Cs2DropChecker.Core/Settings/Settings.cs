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
    }
}
