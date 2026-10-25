using System.Diagnostics;

namespace Cs2DropChecker.App.Services;

/// <summary>Checks every few seconds whether cs2.exe is running.</summary>
public sealed class Cs2Monitor : IDisposable
{
    private readonly Timer _timer;
    private readonly string _process;
    private int _busy;

    public Cs2Monitor()
    {
        _process = Environment.GetEnvironmentVariable("CS2DC_GAME_PROCESS") is { Length: > 0 } p ? p : "cs2";
        _timer = new Timer(_ => Check(), null, 0, 4000);
    }

    public bool IsRunning { get; private set; }
    public DateTimeOffset? LastSeenRunning { get; private set; }
    public event EventHandler<bool>? RunningChanged;

    private void Check()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var procs = Process.GetProcessesByName(_process);
            var running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (running) LastSeenRunning = DateTimeOffset.UtcNow;
            if (running != IsRunning)
            {
                IsRunning = running;
                RunningChanged?.Invoke(this, running);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
