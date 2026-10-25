using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DropChecker.App.Services;
using Cs2DropChecker.App.Views;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Settings;
using Cs2DropChecker.Core.Steam;
using Cs2DropChecker.Core.Util;

namespace Cs2DropChecker.App.ViewModels;

/// <summary>First-run wizard: profile (required), Steam sign-in (optional), basics.</summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly AppServices _s;
    private CancellationTokenSource? _cts;
    private int _reached;

    public SetupViewModel(AppServices s)
    {
        _s = s;
        var settings = s.Settings;
        _input = settings.SteamId;
        _currency = Choices.Pick(Choices.Currencies, settings.Currency);
        _startWithWindows = settings.StartWithWindows;
        _position = settings.Overlay.Position;
        Hotkeys =
        [
            new HotkeyHint("Overlay on / off", PartsOf(settings.Hotkeys.ToggleOverlay)),
            new HotkeyHint("Show total value", PartsOf(settings.Hotkeys.ShowTotalValue)),
            new HotkeyHint("Check inventory now", PartsOf(settings.Hotkeys.RefreshNow)),
        ];
        Steps =
        [
            new SetupStep(0, "Steam profile", "Required"),
            new SetupStep(1, "Instant trades", "Optional"),
            new SetupStep(2, "Basics", "Currency, hotkeys"),
        ];
        UpdateSteps();
        if (_input.Length > 0) _ = CheckAsync();
    }

    private static IReadOnlyList<string> PartsOf(string chord) =>
        Chord.TryParse(chord, out var c) ? c.Parts : chord.Length > 0 ? [chord] : ["Not set"];

    public event EventHandler? Completed;

    public IReadOnlyList<Option> CurrencyOptions => Choices.Currencies;
    public IReadOnlyList<HotkeyHint> Hotkeys { get; }
    public IReadOnlyList<SetupStep> Steps { get; }
    public bool CanSignIn { get; } = SteamSession.RuntimeVersion is not null;

    [ObservableProperty] private int _step;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private SteamProfile? _profile;
    [ObservableProperty] private string _initials = "";
    [ObservableProperty] private string _idLine = "";
    [ObservableProperty] private string _inventoryLine = "";
    [ObservableProperty] private string _inventoryKind = "";
    [ObservableProperty] private bool _signedIn;
    [ObservableProperty] private string _signInNote = "";
    [ObservableProperty] private bool _signInBusy;
    [ObservableProperty] private Option _currency;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private OverlayPosition _position;

    partial void OnStepChanged(int value)
    {
        _reached = Math.Max(_reached, value);
        UpdateSteps();
    }

    partial void OnProfileChanged(SteamProfile? value) => UpdateSteps();

    private void UpdateSteps()
    {
        foreach (var s in Steps)
            s.State = s.Index == Step ? "on" : s.Index < _reached || (s.Index == 0 && Profile is not null && Step > 0) ? "done" : "";
    }

    partial void OnInputChanged(string value)
    {
        _cts?.Cancel();
        Error = "";
        if (Profile is not null && value.Trim() != Profile.SteamId.ToString(CultureInfo.InvariantCulture)) Profile = null;
        if (!SteamIdInput.TryParse(value, out _, out _)) return;
        var cts = _cts = new CancellationTokenSource();
        _ = DebouncedCheckAsync(cts.Token);
    }

    private async Task DebouncedCheckAsync(CancellationToken ct)
    {
        try { await Task.Delay(700, ct); }
        catch (OperationCanceledException) { return; }
        await CheckCoreAsync(ct);
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        await CheckCoreAsync(cts.Token);
    }

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        var text = Input.Trim();
        if (text.Length == 0)
        {
            Error = "Paste your Steam profile link, SteamID64 or custom profile name.";
            return;
        }
        Busy = true;
        Error = "";
        try
        {
            var profile = await _s.Profiles.ResolveAsync(text, ct);
            if (ct.IsCancellationRequested) return;
            Initials = ItemText.Initials(profile.Name);
            IdLine = "SteamID64 " + SteamIdMask.Mask(profile.SteamId);
            InventoryLine = "Checking inventory…";
            InventoryKind = "";
            Profile = profile;
            var r = await _s.Fetcher.FetchAsync(_s.HttpTransport, profile.SteamId, [InventoryFetcher.MainContext], ct);
            if (ct.IsCancellationRequested) return;
            (InventoryLine, InventoryKind) = r.Status switch
            {
                FetchStatus.Ok => ($"Inventory public · {r.Snapshot!.Items.Count} CS2 items", "ok"),
                FetchStatus.Private => ("Inventory private · set it to public in Steam, or sign in on the next step", "warn"),
                FetchStatus.NotFound => ("No CS2 inventory yet · new items still get detected", "warn"),
                FetchStatus.RateLimited => ("Steam is busy right now · the inventory is checked again later", "warn"),
                _ => ("Inventory not reachable right now · " + r.Message, "warn"),
            };
        }
        catch (SteamLookupException ex)
        {
            if (!ct.IsCancellationRequested) Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Error = "Could not reach Steam: " + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) Busy = false;
        }
    }

    [RelayCommand]
    private void NotMe()
    {
        _cts?.Cancel();
        Busy = false;
        Profile = null;
        Input = "";
        Error = "";
    }

    [RelayCommand]
    private void ThisIsMe()
    {
        if (Profile is null) return;
        Step = 1;
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (Profile is null) return;
        SignInBusy = true;
        SignInNote = "";
        try
        {
            var ok = await SignInWindow.RunAsync(_s);
            if (!ok) return;
            if (_s.Session.SignedInSteamId != Profile.SteamId)
            {
                SignInNote = "You signed in with a different Steam account than the profile from step 1. Sign out in Settings and try again, or skip this step.";
                return;
            }
            SignedIn = true;
            Step = 2;
        }
        finally
        {
            SignInBusy = false;
        }
    }

    [RelayCommand]
    private void Skip() => Step = 2;

    [RelayCommand]
    private void Back() => Step = Math.Max(0, Step - 1);

    [RelayCommand]
    private void GoTo(SetupStep? step)
    {
        if (step is null) return;
        var n = step.Index;
        if (n == 0 || (Profile is not null && n <= Math.Max(_reached, 1))) Step = n;
    }

    [RelayCommand]
    private void SetPosition(string position)
    {
        if (Enum.TryParse<OverlayPosition>(position, out var p)) Position = p;
    }

    [RelayCommand]
    private void Finish()
    {
        if (Profile is not { } p) return;
        var changedAccount = _s.Settings.SteamId != p.SteamId.ToString(CultureInfo.InvariantCulture);
        _s.Mutate(x =>
        {
            x.SteamId = p.SteamId.ToString(CultureInfo.InvariantCulture);
            x.ProfileName = p.Name;
            x.AvatarUrl = p.AvatarUrl;
            x.UseSignIn = SignedIn;
            x.Currency = Currency.Value as string ?? "EUR";
            x.StartWithWindows = StartWithWindows;
            x.Overlay.Position = Position;
            x.FirstRunDone = true;
        });
        if (!changedAccount) _s.Watcher.PollNow();
        Completed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record HotkeyHint(string Label, IReadOnlyList<string> Parts);

public sealed partial class SetupStep(int index, string title, string subtitle) : ObservableObject
{
    public int Index { get; } = index;
    public string Number { get; } = (index + 1).ToString(CultureInfo.InvariantCulture);
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    [ObservableProperty] private string _state = "";
}
