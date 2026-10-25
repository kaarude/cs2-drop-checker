using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Services;

/// <summary>Windows notifications through the tray icon. Call on the UI thread.</summary>
public sealed class Notifier(ILogger log)
{
    public TaskbarIcon? Tray { get; set; }

    public void Show(string title, string body)
    {
        var tray = Tray;
        if (tray is null) return;
        try { tray.ShowNotification(title, body, NotificationIcon.Info); }
        catch (Exception ex) { log.LogInformation("Notification failed: {Message}", ex.Message); }
    }
}
