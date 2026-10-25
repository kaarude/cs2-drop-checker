using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Cs2DropChecker.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Services;

public enum HotkeyAction { ToggleOverlay, ShowTotalValue, RefreshNow, ShareLastDrop }

public readonly record struct Chord(ModifierKeys Modifiers, Key Key)
{
    private static readonly Dictionary<Key, string> Names = new()
    {
        [Key.D0] = "0", [Key.D1] = "1", [Key.D2] = "2", [Key.D3] = "3", [Key.D4] = "4",
        [Key.D5] = "5", [Key.D6] = "6", [Key.D7] = "7", [Key.D8] = "8", [Key.D9] = "9",
        [Key.NumPad0] = "Num 0", [Key.NumPad1] = "Num 1", [Key.NumPad2] = "Num 2", [Key.NumPad3] = "Num 3", [Key.NumPad4] = "Num 4",
        [Key.NumPad5] = "Num 5", [Key.NumPad6] = "Num 6", [Key.NumPad7] = "Num 7", [Key.NumPad8] = "Num 8", [Key.NumPad9] = "Num 9",
        [Key.Multiply] = "Num *", [Key.Add] = "Num +", [Key.Subtract] = "Num -", [Key.Divide] = "Num /", [Key.Decimal] = "Num .",
        [Key.OemTilde] = "Tilde", [Key.OemMinus] = "Minus", [Key.OemPlus] = "Plus", [Key.OemComma] = "Comma", [Key.OemPeriod] = "Period",
        [Key.OemQuestion] = "Slash", [Key.OemOpenBrackets] = "[", [Key.OemCloseBrackets] = "]", [Key.OemPipe] = "Backslash",
        [Key.OemSemicolon] = "Semicolon", [Key.OemQuotes] = "Quote", [Key.PageDown] = "PageDown", [Key.PageUp] = "PageUp",
        [Key.Enter] = "Enter", [Key.Back] = "Backspace", [Key.Scroll] = "ScrollLock", [Key.Pause] = "Pause",
        [Key.Insert] = "Insert", [Key.Delete] = "Delete", [Key.Home] = "Home", [Key.End] = "End", [Key.Space] = "Space",
        [Key.Tab] = "Tab", [Key.Up] = "Up", [Key.Down] = "Down", [Key.Left] = "Left", [Key.Right] = "Right",
    };

    private static readonly Dictionary<string, Key> Reverse =
        Names.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

    public static string KeyName(Key key) => Names.TryGetValue(key, out var n) ? n : key.ToString();

    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

    private static bool AllowedAlone(Key k) => k is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll or Key.Insert or Key.Home
        or Key.End or Key.PageUp or Key.PageDown or >= Key.NumPad0 and <= Key.Divide;

    public int KeyCount => 1 + (Modifiers.HasFlag(ModifierKeys.Control) ? 1 : 0) + (Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 0)
        + (Modifiers.HasFlag(ModifierKeys.Alt) ? 1 : 0) + (Modifiers.HasFlag(ModifierKeys.Windows) ? 1 : 0);

    public bool IsValid => Key != Key.None && !IsModifierKey(Key) && KeyCount <= 3 && (Modifiers != ModifierKeys.None || AllowedAlone(Key));

    public string? Problem =>
        Key == Key.None || IsModifierKey(Key) ? "Press a key together with the modifiers."
        : KeyCount > 3 ? "Use at most 3 keys."
        : Modifiers == ModifierKeys.None && !AllowedAlone(Key) ? "A single letter or digit would block typing. Add Ctrl, Shift or Alt, or use F1–F24."
        : null;

    public uint Vk => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Control) ? Native.MOD_CONTROL : 0) | (Modifiers.HasFlag(ModifierKeys.Shift) ? Native.MOD_SHIFT : 0)
        | (Modifiers.HasFlag(ModifierKeys.Alt) ? Native.MOD_ALT : 0) | (Modifiers.HasFlag(ModifierKeys.Windows) ? Native.MOD_WIN : 0);

    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            if (Key != Key.None) parts.Add(KeyName(Key));
            return parts;
        }
    }

    public override string ToString() => string.Join("+", Parts);

    public static bool TryParse(string? text, out Chord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = ModifierKeys.None;
        var key = Key.None;
        var raw = text.Replace("Num +", "Num Plus", StringComparison.OrdinalIgnoreCase);
        foreach (var part0 in raw.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var part = part0.Equals("Num Plus", StringComparison.OrdinalIgnoreCase) ? "Num +" : part0;
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; continue;
                case "shift": mods |= ModifierKeys.Shift; continue;
                case "alt": mods |= ModifierKeys.Alt; continue;
                case "win" or "windows": mods |= ModifierKeys.Windows; continue;
            }
            if (Reverse.TryGetValue(part, out var named)) key = named;
            else if (Enum.TryParse<Key>(part, true, out var parsed)) key = parsed;
            else return false;
        }
        chord = new Chord(mods, key);
        return key != Key.None;
    }

    public static Chord FromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        return new Chord(Keyboard.Modifiers, IsModifierKey(key) ? Key.None : key);
    }
}

/// <summary>
/// Global shortcuts via RegisterHotKey. When another program already owns a shortcut, a low-level keyboard hook
/// listens for it instead (that path does not swallow the key).
/// </summary>
public sealed class HotkeyService(Dispatcher ui, ILogger log) : IDisposable
{
    private HwndSource? _src;
    private readonly Dictionary<int, HotkeyAction> _ids = new();
    private readonly Dictionary<HotkeyAction, Chord> _chords = new();
    private readonly List<(Chord Chord, HotkeyAction Action)> _hooked = new();
    private IntPtr _hook;
    private Native.LowLevelKeyboardProc? _proc;
    private uint _downVk;
    private HotkeySettings? _settings;
    private bool _suspended;

    public event Action<HotkeyAction>? Pressed;

    public IReadOnlyCollection<HotkeyAction> HookedActions => _hooked.Select(h => h.Action).ToList();

    public void Initialize()
    {
        if (_src is not null) return;
        _src = new HwndSource(new HwndSourceParameters("cs2dc-hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _src.AddHook(WndProc);
    }

    public void Apply(HotkeySettings settings)
    {
        _settings = settings;
        if (_suspended) return;
        UnregisterAll();
        RegisterAll(settings);
    }

    public void Suspend()
    {
        _suspended = true;
        UnregisterAll();
    }

    public void Resume()
    {
        _suspended = false;
        if (_settings is not null) Apply(_settings);
    }

    public static IEnumerable<(HotkeyAction Action, string Text)> Entries(HotkeySettings s) =>
    [
        (HotkeyAction.ToggleOverlay, s.ToggleOverlay),
        (HotkeyAction.ShowTotalValue, s.ShowTotalValue),
        (HotkeyAction.RefreshNow, s.RefreshNow),
        (HotkeyAction.ShareLastDrop, s.ShareLastDrop),
    ];

    /// <summary>True when no other program has registered the chord.</summary>
    public bool IsFree(Chord chord)
    {
        if (_src is null || !chord.IsValid) return true;
        if (_chords.Values.Contains(chord) && !_hooked.Any(h => h.Chord == chord)) return true;
        const int probe = 0x7F00;
        if (!Native.RegisterHotKey(_src.Handle, probe, chord.NativeModifiers | Native.MOD_NOREPEAT, chord.Vk)) return false;
        Native.UnregisterHotKey(_src.Handle, probe);
        return true;
    }

    private void RegisterAll(HotkeySettings s)
    {
        if (_src is null) return;
        var id = 1;
        foreach (var (action, text) in Entries(s))
        {
            if (!Chord.TryParse(text, out var chord) || !chord.IsValid) continue;
            _chords[action] = chord;
            if (Native.RegisterHotKey(_src.Handle, id, chord.NativeModifiers | Native.MOD_NOREPEAT, chord.Vk))
            {
                _ids[id] = action;
            }
            else
            {
                log.LogInformation("Shortcut {Chord} is taken by another program; listening with a keyboard hook", chord);
                _hooked.Add((chord, action));
            }
            id++;
        }
        if (_hooked.Count > 0) InstallHook();
    }

    private void UnregisterAll()
    {
        if (_src is not null)
            foreach (var id in _ids.Keys) Native.UnregisterHotKey(_src.Handle, id);
        _ids.Clear();
        _chords.Clear();
        _hooked.Clear();
        RemoveHook();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _ids.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }
        return IntPtr.Zero;
    }

    private void InstallHook()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = HookProc;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) log.LogWarning("Keyboard hook could not be installed (error {Code})", Marshal.GetLastWin32Error());
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            var vk = (uint)Marshal.ReadInt32(lParam);
            if (msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
            {
                if (vk != _downVk)
                {
                    _downVk = vk;
                    var mods = CurrentModifiers();
                    foreach (var (chord, action) in _hooked)
                        if (chord.Vk == vk && chord.Modifiers == mods)
                            ui.BeginInvoke(() => Pressed?.Invoke(action));
                }
            }
            else if (msg is Native.WM_KEYUP or Native.WM_SYSKEYUP && vk == _downVk)
            {
                _downVk = 0;
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static ModifierKeys CurrentModifiers()
    {
        var m = ModifierKeys.None;
        if (Native.GetAsyncKeyState(0x11) < 0) m |= ModifierKeys.Control;
        if (Native.GetAsyncKeyState(0x10) < 0) m |= ModifierKeys.Shift;
        if (Native.GetAsyncKeyState(0x12) < 0) m |= ModifierKeys.Alt;
        if (Native.GetAsyncKeyState(0x5B) < 0 || Native.GetAsyncKeyState(0x5C) < 0) m |= ModifierKeys.Windows;
        return m;
    }

    public void Dispose()
    {
        UnregisterAll();
        _src?.Dispose();
        _src = null;
    }
}
