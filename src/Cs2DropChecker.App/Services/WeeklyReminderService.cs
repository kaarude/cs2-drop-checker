using System.Globalization;
using System.Windows.Threading;
using Cs2DropChecker.Core.Weekly;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Services;

/// <summary>Reminds the user once per CS2 week, at the chosen time, when no weekly drop has arrived since the reset.</summary>
public sealed class WeeklyReminderService(AppServices s, ILogger log) : IDisposable
{
    private DispatcherTimer? _timer;
    private DateTimeOffset? _satisfiedWeek;

    public void Start()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background, s.Ui) { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    private void Check()
    {
        var settings = s.Settings;
        var r = settings.WeeklyReminder;
        var id = settings.SteamId64;
        if (!r.Enabled || id == 0 || !settings.FirstRunDone) return;
        if (s.Watcher.State is WatchState.NotSetUp or WatchState.Private or WatchState.NotFound) return;
        var now = DateTimeOffset.UtcNow;
        var start = Cs2Week.Start(now);
        if (_satisfiedWeek == start) return;
        var zone = TimeZoneInfo.Local;
        if (!WeeklyReminder.IsDue(now, false, r.LastShownWeek, r.Day, r.TimeOfDay, zone)) return;

        bool hasDrop;
        try { hasDrop = WeeklyReminder.HasWeeklyDrop(s.Db.GetDrops(id, 3000).Where(d => d.SeenAt >= start), now); }
        catch (Exception ex)
        {
            log.LogWarning("Weekly reminder check failed: {Message}", ex.Message);
            return;
        }
        if (hasDrop)
        {
            _satisfiedWeek = start;
            return;
        }

        var next = Cs2Week.NextReset(now).ToLocalTime();
        var title = "Weekly drop not claimed yet";
        var body = $"No weekly drop since the reset. Play a match before it resets on {next.ToString("dddd HH:mm", CultureInfo.InvariantCulture)}.";
        log.LogInformation("Weekly drop reminder shown");
        s.Notifier.Show(title, body);
        if (r.ShowOverlayCard && settings.Overlay.Enabled) s.Overlay?.ShowNotice(title, body);
        s.Mutate(x => x.WeeklyReminder.LastShownWeek = start);
    }

    public void Dispose() => _timer?.Stop();
}
