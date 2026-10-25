using Cs2DropChecker.Core;
using Microsoft.Win32;

namespace Cs2DropChecker.App.Services;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CS2 Drop Checker";

    /// <summary>Test and portable runs (CS2DC_HOME set) never touch the registry.</summary>
    public static bool Allowed => AppPaths.HomeOverride is null;

    public static void Apply(bool enabled)
    {
        if (!Allowed) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
        }
    }
}

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _wait;

    public SingleInstance()
    {
        var id = "CS2DropChecker";
        if (AppPaths.HomeOverride is { } home)
            id += "-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(home.ToLowerInvariant())))[..10];
        _mutex = new Mutex(true, $@"Local\{id}.mutex", out var created);
        IsFirst = created;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{id}.show");
    }

    public bool IsFirst { get; }

    public void SignalFirst() => _show.Set();

    public void ListenForShow(Action onShow) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => onShow(), null, -1, false);

    public void Dispose()
    {
        _wait?.Unregister(null);
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch (Exception) { }
        }
        _mutex.Dispose();
        _show.Dispose();
    }
}
