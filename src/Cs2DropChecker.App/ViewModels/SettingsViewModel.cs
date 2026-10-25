using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.App.Views;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Alerts;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Profit;
using Cs2DropChecker.Core.Settings;
using Cs2DropChecker.Core.Weekly;
using Cs2DropChecker.Core.Steam;

namespace Cs2DropChecker.App.ViewModels;

public sealed partial class SourceRow(string id, string name) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private int _index;
}

public sealed partial class HotkeyRow(HotkeyAction action, string label) : ObservableObject
{
    public HotkeyAction Action { get; } = action;
    public string Label { get; } = label;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private IReadOnlyList<string> _parts = [];
    [ObservableProperty] private IReadOnlyList<string> _liveParts = [];
    [ObservableProperty] private bool _recording;

    partial void OnTextChanged(string value) =>
        Parts = Chord.TryParse(value, out var c) ? c.Parts : value.Length > 0 ? [value] : ["Not set"];
}

public static class Choices
{
    public static readonly IReadOnlyList<Option> Currencies =
        Core.Pricing.Currencies.All.Select(c => new Option(c.Code, $"{c.Code} {c.Symbol.Trim()}")).ToList();

    public static readonly IReadOnlyList<Option> PollIntervals =
        new[] { 2, 3, 5, 10, 15, 30 }.Select(m => new Option(m, $"{m} min")).ToList();

    public static readonly IReadOnlyList<Option> CardSeconds =
        new[] { 5, 8, 10, 15, 20, 30 }.Select(m => new Option(m, $"{m} s")).ToList();

    public static readonly IReadOnlyList<Option> CloseActions = [new(true, "To tray"), new(false, "Quit app")];

    public static readonly IReadOnlyList<Option> WhileOff = [new(true, "Queue cards"), new(false, "Skip cards")];

    public static readonly IReadOnlyList<Option> FriendIntervals =
        new[] { 15, 30, 60, 120, 240 }.Select(m => new Option(m, m < 60 ? $"{m} min" : $"{m / 60} h")).ToList();

    public static readonly IReadOnlyList<Option> Days =
        new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new Option(d, d.ToString())).ToList();

    public static Option Pick(IReadOnlyList<Option> options, object value) =>
        options.FirstOrDefault(o => Equals(o.Value, value)) ?? options[0];

    public static Option PickNearest(IReadOnlyList<Option> options, int value) =>
        options.MinBy(o => Math.Abs((int)o.Value - value))!;

    public static string SourceName(string id) => id switch
    {
        PriceSourceIds.SteamLive => "Steam 24 h median, live",
        PriceSourceIds.SteamFeed => "Steam 24 h median, full list",
        _ => "Skinport",
    };
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly HashSet<string> Tracked =
    [
        nameof(SteamInput), nameof(WatchOnlyWhileCs2), nameof(PollInterval), nameof(Currency), nameof(StartWithWindows),
        nameof(CloseAction), nameof(OverlayEnabled), nameof(Position), nameof(OffsetX), nameof(OffsetY), nameof(Monitor),
        nameof(SizePercent), nameof(OpacityPercent), nameof(Seconds), nameof(WhileOff),
        nameof(KeyPriceText), nameof(ShareFolder), nameof(ShareShowProfile), nameof(ShareCopy), nameof(FriendsInterval),
        nameof(AlertToasts), nameof(DiscordUrl), nameof(ReminderEnabled), nameof(ReminderDay), nameof(ReminderTime), nameof(ReminderOverlay),
    ];

    private readonly AppServices _s;
    private readonly DispatcherTimer _savedTimer;
    private bool _loading;

    public SettingsViewModel(AppServices s)
    {
        _s = s;
        _savedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _savedTimer.Tick += (_, _) =>
        {
            _savedTimer.Stop();
            SavedText = "";
        };
        _pollInterval = Choices.PollIntervals[1];
        _currency = Choices.Currencies[0];
        _closeAction = Choices.CloseActions[0];
        _seconds = Choices.CardSeconds[2];
        _whileOff = Choices.WhileOff[0];
        _monitor = new Option(0, "Primary");
        _friendsInterval = Choices.FriendIntervals[1];
        _reminderDay = Choices.Pick(Choices.Days, DayOfWeek.Sunday);
        Hotkeys.Add(new HotkeyRow(HotkeyAction.ToggleOverlay, "Overlay on / off"));
        Hotkeys.Add(new HotkeyRow(HotkeyAction.ShowTotalValue, "Show total value"));
        Hotkeys.Add(new HotkeyRow(HotkeyAction.RefreshNow, "Refresh now"));
        Hotkeys.Add(new HotkeyRow(HotkeyAction.ShareLastDrop, "Share last drop"));
        foreach (var h in Hotkeys)
            h.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HotkeyRow.Text) && !_loading) IsDirty = true;
            };
        s.Watcher.InventoryChanged += (_, _) => UpdateAccountStatus();
        s.Watcher.StatusChanged += (_, _) => UpdateAccountStatus();
        s.Prices.PricesUpdated += (_, _) => s.Ui.BeginInvoke(UpdateSourceStatus);
        Load(onlyIfClean: false);
    }

    public IReadOnlyList<Option> CurrencyOptions => Choices.Currencies;
    public IReadOnlyList<Option> PollOptions => Choices.PollIntervals;
    public IReadOnlyList<Option> CloseOptions => Choices.CloseActions;
    public IReadOnlyList<Option> SecondsOptions => Choices.CardSeconds;
    public IReadOnlyList<Option> WhileOffOptions => Choices.WhileOff;
    public IReadOnlyList<Option> FriendIntervalOptions => Choices.FriendIntervals;
    public IReadOnlyList<Option> DayOptions => Choices.Days;
    public ObservableCollection<Option> Monitors { get; } = new();
    public ObservableCollection<SourceRow> Sources { get; } = new();
    public ObservableCollection<HotkeyRow> Hotkeys { get; } = new();
    public HotkeyRow? Recording { get; private set; }

    [ObservableProperty] private string _steamInput = "";
    [ObservableProperty] private string _accountStatus = "";
    [ObservableProperty] private string _accountKind = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _signInText = "";
    [ObservableProperty] private string _signInKind = "";
    [ObservableProperty] private bool _signedIn;
    [ObservableProperty] private bool _signInBusy;
    [ObservableProperty] private bool _watchOnlyWhileCs2;
    [ObservableProperty] private Option _pollInterval;
    [ObservableProperty] private Option _currency;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private Option _closeAction;
    [ObservableProperty] private bool _overlayEnabled;
    [ObservableProperty] private OverlayPosition _position;
    [ObservableProperty] private string _offsetX = "16";
    [ObservableProperty] private string _offsetY = "16";
    [ObservableProperty] private Option _monitor;
    [ObservableProperty] private double _sizePercent = 80;
    [ObservableProperty] private double _opacityPercent = 96;
    [ObservableProperty] private Option _seconds;
    [ObservableProperty] private Option _whileOff;
    [ObservableProperty] private string _hotkeyMessage = "";
    [ObservableProperty] private string _hotkeyKind = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _keyPriceText = "";
    [ObservableProperty] private string _keyPriceNote = "";
    [ObservableProperty] private string _shareFolder = "";
    [ObservableProperty] private bool _shareShowProfile;
    [ObservableProperty] private bool _shareCopy;
    [ObservableProperty] private Option _friendsInterval;
    [ObservableProperty] private bool _alertToasts;
    [ObservableProperty] private string _discordUrl = "";
    [ObservableProperty] private string _discordStatus = "";
    [ObservableProperty] private string _discordKind = "";
    [ObservableProperty] private bool _reminderEnabled;
    [ObservableProperty] private Option _reminderDay;
    [ObservableProperty] private string _reminderTime = "19:00";
    [ObservableProperty] private bool _reminderOverlay;
    [ObservableProperty] private string _reminderNote = "";
    [ObservableProperty] private string _formMessage = "";
    [ObservableProperty] private string _savedText = "";

    public string SizeText => $"{Math.Round(SizePercent)}%";
    public string OpacityText => $"{Math.Round(OpacityPercent)}%";
    public string CurrencyNote => (Currency.Value as string) == "USD"
        ? "Non-USD sources are converted with the daily ECB rate"
        : $"Non-{Currency.Value} sources are converted with the daily ECB rate";

    partial void OnSizePercentChanged(double value) => OnPropertyChanged(nameof(SizeText));
    partial void OnOpacityPercentChanged(double value) => OnPropertyChanged(nameof(OpacityText));

    partial void OnCurrencyChanged(Option value)
    {
        OnPropertyChanged(nameof(CurrencyNote));
        UpdateSourceStatus();
        UpdateKeyNote();
    }

    partial void OnKeyPriceTextChanged(string value) => UpdateKeyNote();

    partial void OnDiscordUrlChanged(string value)
    {
        var url = value.Trim();
        DiscordStatus = url.Length == 0 ? "Off" : DiscordWebhook.IsValidUrl(url) ? "Looks like a Discord webhook" : "Not a Discord webhook URL";
        DiscordKind = url.Length == 0 ? "" : DiscordWebhook.IsValidUrl(url) ? "ok" : "warn";
    }

    private void UpdateKeyNote()
    {
        var cur = Currency.Value as string ?? "EUR";
        var store = KeyPrices.Effective(null, null, cur, _s.Prices.Fx.Rate);
        KeyPriceNote = KeyPriceText.Trim().Length == 0
            ? "Empty uses Valve's store price" + (store is { } st ? $" ({Money.Format(st, cur)})" : "") + ". Keys can't be resold, so there is no market price."
            : AlertsViewModel.ParseAmount(KeyPriceText) is null ? "Enter a price such as 2.35" : $"In {cur}. Used for new openings; past openings keep their price.";
    }

    private void UpdateReminderNote()
    {
        var reset = Cs2Week.NextReset(DateTimeOffset.UtcNow).ToLocalTime();
        ReminderNote = $"Weekly drops reset every {reset.DayOfWeek} at {reset.ToString("HH:mm", CultureInfo.InvariantCulture)} your time (Wednesday 01:00 UTC). One reminder per week, only if no weekly drop arrived.";
    }

    partial void OnSteamInputChanged(string value)
    {
        if (_loading) return;
        if (value.Trim() == _s.Settings.SteamId) UpdateAccountStatus();
        else
        {
            AccountStatus = "Checked with Steam when you save";
            AccountKind = "";
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is { } n && Tracked.Contains(n)) IsDirty = true;
    }

    public void Load(bool onlyIfClean)
    {
        if (onlyIfClean && IsDirty)
        {
            UpdateAccountStatus();
            UpdateSession();
            UpdateSourceStatus();
            return;
        }
        CancelRecording();
        _loading = true;
        try
        {
            var s = _s.Settings;
            SteamInput = s.SteamId;
            WatchOnlyWhileCs2 = s.WatchOnlyWhileCs2Running;
            PollInterval = Choices.PickNearest(Choices.PollIntervals, s.PollIntervalMinutes);
            Currency = Choices.Pick(Choices.Currencies, s.Currency);
            StartWithWindows = s.StartWithWindows;
            CloseAction = Choices.Pick(Choices.CloseActions, s.CloseToTray);

            var o = s.Overlay;
            OverlayEnabled = o.Enabled;
            Position = o.Position;
            OffsetX = o.OffsetX.ToString(CultureInfo.InvariantCulture);
            OffsetY = o.OffsetY.ToString(CultureInfo.InvariantCulture);
            SizePercent = o.SizePercent;
            OpacityPercent = o.OpacityPercent;
            Seconds = Choices.PickNearest(Choices.CardSeconds, o.SecondsPerCard);
            WhileOff = Choices.Pick(Choices.WhileOff, o.QueueWhileOff);
            LoadMonitors(o.Monitor);

            KeyPriceText = s.Profit.KeyPrice is { } k
                ? (s.Profit.KeyPriceCurrency is { } kc && kc != s.Currency ? k * (_s.Prices.Fx.Rate(kc, s.Currency) ?? 1m) : k).ToString("0.00", CultureInfo.InvariantCulture)
                : "";
            ShareFolder = s.Share.Folder;
            ShareShowProfile = s.Share.ShowProfile;
            ShareCopy = s.Share.CopyToClipboard;
            FriendsInterval = Choices.PickNearest(Choices.FriendIntervals, s.Friends.RefreshMinutes);
            AlertToasts = s.Alerts.Toasts;
            DiscordUrl = s.Alerts.DiscordWebhookUrl;
            var r = s.WeeklyReminder;
            ReminderEnabled = r.Enabled;
            ReminderDay = Choices.Pick(Choices.Days, r.Day);
            ReminderTime = r.Time;
            ReminderOverlay = r.ShowOverlayCard;
            FormMessage = "";

            Sources.Clear();
            foreach (var p in s.PriceSources)
            {
                var row = new SourceRow(p.Id, Choices.SourceName(p.Id)) { Enabled = p.Enabled };
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SourceRow.Enabled) && !_loading)
                    {
                        IsDirty = true;
                        UpdateSourceStatus();
                    }
                };
                Sources.Add(row);
            }

            foreach (var h in Hotkeys)
            {
                h.Text = h.Action switch
                {
                    HotkeyAction.ToggleOverlay => s.Hotkeys.ToggleOverlay,
                    HotkeyAction.ShowTotalValue => s.Hotkeys.ShowTotalValue,
                    HotkeyAction.ShareLastDrop => s.Hotkeys.ShareLastDrop,
                    _ => s.Hotkeys.RefreshNow,
                };
                h.Recording = false;
                h.LiveParts = [];
            }
            HotkeyMessage = "";
            HotkeyKind = "";
        }
        finally
        {
            _loading = false;
        }
        IsDirty = false;
        UpdateAccountStatus();
        UpdateSession();
        UpdateSourceStatus();
        UpdateKeyNote();
        UpdateReminderNote();
    }

    private void LoadMonitors(int selected)
    {
        Monitors.Clear();
        var list = Native.GetMonitors();
        for (var i = 0; i < list.Count; i++)
        {
            var b = list[i].Bounds;
            Monitors.Add(new Option(i, (i == 0 ? "Primary" : $"Monitor {i + 1}") + $" · {b.Width}×{b.Height}"));
        }
        if (Monitors.Count == 0) Monitors.Add(new Option(0, "Primary"));
        Monitor = Monitors.FirstOrDefault(m => (int)m.Value == selected) ?? Monitors[0];
    }

    private void UpdateAccountStatus()
    {
        if (_loading) return;
        if (SteamInput.Trim() != _s.Settings.SteamId) return;
        var w = _s.Watcher;
        if (_s.Settings.SteamId64 == 0)
        {
            AccountStatus = "No profile set";
            AccountKind = "warn";
        }
        else if (w.State == WatchState.Private)
        {
            AccountStatus = "Inventory private";
            AccountKind = "warn";
        }
        else if (w.State == WatchState.NotFound)
        {
            AccountStatus = "No CS2 inventory";
            AccountKind = "warn";
        }
        else if (w.Current is { } snap)
        {
            AccountStatus = (snap.SignedIn ? "Signed in" : "Inventory public") + $" · {snap.Items.Count} items";
            AccountKind = "ok";
        }
        else
        {
            AccountStatus = "Not checked yet";
            AccountKind = "";
        }
    }

    public void UpdateSession()
    {
        var session = _s.Session;
        var s = _s.Settings;
        if (session.IsSignedIn && session.SignedInSteamId == s.SteamId64)
        {
            SignedIn = true;
            SignInText = "Signed in · trades show instantly";
            SignInKind = "ok";
        }
        else if (session.IsSignedIn)
        {
            SignedIn = true;
            SignInText = "Signed in as another account · public view used";
            SignInKind = "warn";
        }
        else if (s.UseSignIn)
        {
            SignedIn = false;
            SignInText = "Sign-in expired · trades show after 7 days";
            SignInKind = "warn";
        }
        else
        {
            SignedIn = false;
            SignInText = "Not signed in · trades show after 7 days";
            SignInKind = "";
        }
    }

    private void UpdateSourceStatus()
    {
        var cur = Currency.Value as string ?? "EUR";
        var first = true;
        var i = 1;
        foreach (var row in Sources)
        {
            row.Index = i++;
            row.Detail = row.Id switch
            {
                PriceSourceIds.SteamLive => $"new drops · {cur} · max 1 per 5 s",
                PriceSourceIds.SteamFeed => cur == "USD" ? "CSGO Trader feed · USD · every 6 h" : $"CSGO Trader feed · USD → {cur} every 6 h",
                _ => $"full list · {cur} · every 10 min",
            };
            var updated = row.Id switch
            {
                PriceSourceIds.SteamLive => null,
                PriceSourceIds.SteamFeed => _s.Prices.Feed.LastUpdated,
                _ => _s.Prices.Skinport.LastUpdated,
            };
            var status = row.Id == PriceSourceIds.SteamLive ? "on each drop" : updated is { } u ? When.Ago(u) : "not loaded";
            row.Status = !row.Enabled ? "off" : first ? status : "fallback · " + status;
            if (row.Enabled) first = false;
        }
    }

    public void MoveSource(SourceRow row, int newIndex)
    {
        var old = Sources.IndexOf(row);
        if (old < 0) return;
        newIndex = Math.Clamp(newIndex, 0, Sources.Count - 1);
        if (old == newIndex) return;
        Sources.Move(old, newIndex);
        IsDirty = true;
        UpdateSourceStatus();
    }

    [RelayCommand]
    private void MoveUp(SourceRow row) => MoveSource(row, Sources.IndexOf(row) - 1);

    [RelayCommand]
    private void MoveDown(SourceRow row) => MoveSource(row, Sources.IndexOf(row) + 1);

    [RelayCommand]
    private void SetPosition(string position)
    {
        if (Enum.TryParse<OverlayPosition>(position, out var p)) Position = p;
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        SignInBusy = true;
        try
        {
            var ok = await SignInWindow.RunAsync(_s);
            if (ok)
            {
                _s.Mutate(x => x.UseSignIn = true);
                _s.Watcher.PollNow();
            }
        }
        finally
        {
            SignInBusy = false;
            UpdateSession();
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        SignInBusy = true;
        try
        {
            await _s.Session.SignOutAsync();
            _s.Mutate(x => x.UseSignIn = false);
        }
        finally
        {
            SignInBusy = false;
            UpdateSession();
        }
    }

    [RelayCommand]
    private void ShowTestCard() => _s.Overlay?.ShowTestCard(BuildOverlay());

    [RelayCommand]
    private void BrowseShareFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Folder for share cards",
            InitialDirectory = ShareFolder.Length > 0 && System.IO.Directory.Exists(ShareFolder) ? ShareFolder : null,
        };
        if (dialog.ShowDialog() == true) ShareFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenShareFolder()
    {
        var folder = ShareFolder.Trim().Length > 0 ? ShareFolder.Trim() : ShareCardService.DefaultFolder;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    [RelayCommand]
    private async Task TestDiscordAsync()
    {
        var url = DiscordUrl.Trim();
        if (!DiscordWebhook.IsValidUrl(url))
        {
            DiscordStatus = "Paste a Discord webhook URL first";
            DiscordKind = "warn";
            return;
        }
        DiscordStatus = "Sending a test message…";
        DiscordKind = "";
        var status = await _s.Alerts.SendDiscordAsync(url, "CS2 Drop Checker test", "Price alerts will be posted here.");
        DiscordStatus = status is >= 200 and < 300 ? "Test message sent" : status < 0 ? "Discord could not be reached" : $"Discord answered {status}";
        DiscordKind = status is >= 200 and < 300 ? "ok" : "warn";
    }

    [RelayCommand]
    private void ShowTestReminder() =>
        _s.Overlay?.ShowNotice("Weekly drop not claimed yet", "Test reminder. The real one appears once a week if no weekly drop arrived.");

    [RelayCommand]
    private void Discard() => Load(onlyIfClean: false);

    [RelayCommand]
    private void Record(HotkeyRow row)
    {
        if (Recording == row)
        {
            CancelRecording();
            return;
        }
        CancelRecording();
        Recording = row;
        row.Recording = true;
        row.LiveParts = [];
        _s.Hotkeys.Suspend();
        HotkeyMessage = "Press 1–3 keys together. Esc cancels.";
        HotkeyKind = "";
    }

    public void CancelRecording()
    {
        if (Recording is null) return;
        Recording.Recording = false;
        Recording.LiveParts = [];
        Recording = null;
        _s.Hotkeys.Resume();
        HotkeyMessage = "";
        HotkeyKind = "";
    }

    /// <summary>Called by the window for every key press while a shortcut is being recorded.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var row = Recording;
        if (row is null) return false;
        var chord = Chord.FromKeyEvent(e);
        if (chord.Key == Key.Escape && chord.Modifiers == ModifierKeys.None)
        {
            CancelRecording();
            return true;
        }
        row.LiveParts = chord.Parts;
        if (chord.Key == Key.None) return true;
        if (chord.Problem is { } problem)
        {
            HotkeyMessage = problem;
            HotkeyKind = "warn";
            return true;
        }
        var text = chord.ToString();
        var clash = Hotkeys.FirstOrDefault(h => h != row && string.Equals(h.Text, text, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            HotkeyMessage = $"{text} is already used for {clash.Label.ToLowerInvariant()}.";
            HotkeyKind = "warn";
            return true;
        }
        var free = _s.Hotkeys.IsFree(chord);
        row.Text = text;
        row.Recording = false;
        row.LiveParts = [];
        Recording = null;
        _s.Hotkeys.Resume();
        HotkeyMessage = free
            ? $"{text} is free. Shortcuts work while CS2 has focus. A key used here may still also reach the game, so avoid keys you have bound in CS2."
            : $"Another program already uses {text}. The app still notices it, but that program reacts too.";
        HotkeyKind = free ? "ok" : "warn";
        IsDirty = true;
        return true;
    }

    private OverlaySettings BuildOverlay() => new()
    {
        Enabled = OverlayEnabled,
        Position = Position,
        OffsetX = ParseOffset(OffsetX),
        OffsetY = ParseOffset(OffsetY),
        Monitor = Monitor.Value is int m ? m : 0,
        SizePercent = (int)Math.Round(SizePercent),
        OpacityPercent = (int)Math.Round(OpacityPercent),
        SecondsPerCard = Seconds.Value is int sec ? sec : 10,
        QueueWhileOff = WhileOff.Value is true,
    };

    private static int ParseOffset(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 2000) : 16;

    [RelayCommand]
    private async Task SaveAsync()
    {
        CancelRecording();
        var input = SteamInput.Trim();
        if (input.Length == 0)
        {
            AccountStatus = "Enter your Steam profile link or SteamID64";
            AccountKind = "warn";
            return;
        }
        SteamProfile? profile = null;
        if (input != _s.Settings.SteamId)
        {
            Busy = true;
            AccountStatus = "Checking with Steam…";
            AccountKind = "";
            try
            {
                profile = await _s.Profiles.ResolveAsync(input, CancellationToken.None);
            }
            catch (SteamLookupException ex)
            {
                AccountStatus = ex.Message;
                AccountKind = "warn";
                return;
            }
            catch (Exception ex)
            {
                AccountStatus = "Could not reach Steam: " + ex.Message;
                AccountKind = "warn";
                return;
            }
            finally
            {
                Busy = false;
            }
        }

        var keyText = KeyPriceText.Trim();
        decimal? keyPrice = null;
        if (keyText.Length > 0)
        {
            keyPrice = AlertsViewModel.ParseAmount(keyText);
            if (keyPrice is null or > 1000)
            {
                FormMessage = "Key price: enter a price such as 2.35, or leave it empty.";
                return;
            }
        }
        var hook = DiscordUrl.Trim();
        if (hook.Length > 0 && !DiscordWebhook.IsValidUrl(hook))
        {
            FormMessage = "Discord: paste a webhook URL from Server Settings > Integrations, or leave it empty.";
            return;
        }
        if (!TimeSpan.TryParseExact(ReminderTime.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var remindAt) || remindAt >= TimeSpan.FromDays(1))
        {
            FormMessage = "Weekly reminder: enter a time such as 19:00.";
            return;
        }
        FormMessage = "";

        var overlay = BuildOverlay();
        var keys = Hotkeys.ToDictionary(h => h.Action, h => h.Text);
        var currency = Currency.Value as string ?? "EUR";
        _s.Mutate(x =>
        {
            if (profile is not null)
            {
                x.SteamId = profile.SteamId.ToString(CultureInfo.InvariantCulture);
                x.ProfileName = profile.Name;
                x.AvatarUrl = profile.AvatarUrl;
            }
            x.WatchOnlyWhileCs2Running = WatchOnlyWhileCs2;
            x.PollIntervalMinutes = PollInterval.Value is int p ? p : 3;
            x.Currency = Currency.Value as string ?? "EUR";
            x.PriceSources = Sources.Select(r => new PriceSourceSetting { Id = r.Id, Enabled = r.Enabled }).ToList();
            x.StartWithWindows = StartWithWindows;
            x.CloseToTray = CloseAction.Value is true;
            x.Overlay = overlay;
            x.Hotkeys = new HotkeySettings
            {
                ToggleOverlay = keys[HotkeyAction.ToggleOverlay],
                ShowTotalValue = keys[HotkeyAction.ShowTotalValue],
                RefreshNow = keys[HotkeyAction.RefreshNow],
                ShareLastDrop = keys[HotkeyAction.ShareLastDrop],
            };
            x.Profit.KeyPrice = keyPrice;
            x.Profit.KeyPriceCurrency = keyPrice is null ? null : currency;
            x.Share.Folder = ShareFolder.Trim();
            x.Share.ShowProfile = ShareShowProfile;
            x.Share.CopyToClipboard = ShareCopy;
            x.Friends.RefreshMinutes = FriendsInterval.Value is int fm ? fm : 30;
            x.Alerts.Toasts = AlertToasts;
            x.Alerts.DiscordWebhookUrl = hook;
            x.WeeklyReminder.Enabled = ReminderEnabled;
            x.WeeklyReminder.Day = ReminderDay.Value is DayOfWeek d ? d : DayOfWeek.Sunday;
            x.WeeklyReminder.Time = remindAt.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            x.WeeklyReminder.ShowOverlayCard = ReminderOverlay;
        });
        Load(onlyIfClean: false);
        if (profile is not null)
        {
            AccountStatus = "Saved · checking the new inventory";
            AccountKind = "";
        }
        SavedText = "Saved";
        _savedTimer.Stop();
        _savedTimer.Start();
    }
}
