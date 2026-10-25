using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Cs2DropChecker.Core.Updates;
using Cs2DropChecker.Core.Util;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Services;

public enum UpdateStage { Idle, Downloading, Installing, Failed }

/// <summary>
/// Watches GitHub releases for a newer build and installs it in place: download next to the exe, rename the running
/// exe out of the way (Windows allows renaming a running image, not deleting it), move the new one in, restart.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string AfterUpdateArg = "--after-update";

    private readonly AppServices _s;
    private readonly ILogger _log;
    private readonly HttpClient _http = HttpFactory.Create(timeout: Timeout.InfiniteTimeSpan);
    private readonly UpdateChecker _checker;
    private readonly CancellationTokenSource _cts = new();

    public UpdateService(AppServices s, ILogger log)
    {
        _s = s;
        _log = log;
        _checker = new UpdateChecker(_http);
    }

    public UpdateInfo? Available { get; private set; }
    public UpdateStage Stage { get; private set; }
    public double Progress { get; private set; }
    public string Error { get; private set; } = "";
    public event EventHandler? Changed;

    /// <summary>For testing the installer: a local file in GitHub's release JSON format, whose asset URL may be file:///.</summary>
    private static string? TestFeed => Environment.GetEnvironmentVariable("CS2DC_UPDATE_FEED") is { Length: > 0 } f ? f : null;

    /// <summary>Only the published single-file exe can replace itself; a dev build opens the release page instead.</summary>
    public static bool CanInstallInPlace =>
        string.IsNullOrEmpty(typeof(App).Assembly.Location) && Environment.ProcessPath is { } p
        && Path.GetFileName(p).Equals(UpdateChecker.AssetName, StringComparison.OrdinalIgnoreCase);

    private void Raise() => _s.Ui.BeginInvoke(() => Changed?.Invoke(this, EventArgs.Empty));

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(TestFeed is null ? 20 : 2), ct);
            while (!ct.IsCancellationRequested)
            {
                await CheckAsync(ct);
                await Task.Delay(TimeSpan.FromHours(6), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            UpdateInfo? found;
            if (TestFeed is { } feed)
            {
                found = UpdateChecker.ParseLatest(await File.ReadAllTextAsync(feed, ct), AppVersion.Current, allowAnyUrl: true);
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                found = await _checker.CheckAsync(AppVersion.Current, timeout.Token);
            }
            if (found?.Version != Available?.Version)
                _log.LogInformation(found is null ? "No update available" : "Update available: {Version}", found?.VersionText);
            Available = found;
            Raise();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogInformation("Update check failed: {Message}", ex.Message);
        }
    }

    public async Task InstallAsync()
    {
        if (Available is not { } info || Stage is UpdateStage.Downloading or UpdateStage.Installing) return;
        if (!CanInstallInPlace)
        {
            OpenPage(info.PageUrl);
            return;
        }
        var exe = Environment.ProcessPath!;
        var download = exe + ".download";
        var old = exe + ".old";
        Stage = UpdateStage.Downloading;
        Progress = 0;
        Error = "";
        Raise();
        try
        {
            var last = -1;
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                var pct = (int)(p * 100);
                if (pct == last) return;
                last = pct;
                Raise();
            });
            _log.LogInformation("Downloading update {Version} from {Url}", info.VersionText, info.DownloadUrl);
            await _checker.DownloadAsync(info, download, progress, _cts.Token);

            Stage = UpdateStage.Installing;
            Raise();
            TryDelete(old);
            File.Move(exe, old);
            try
            {
                File.Move(download, exe);
            }
            catch
            {
                File.Move(old, exe);
                throw;
            }
            _log.LogInformation("Update {Version} installed; restarting", info.VersionText);
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            psi.ArgumentList.Add(AfterUpdateArg);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(psi);
            App.Current.ExitApp();
        }
        catch (Exception ex)
        {
            TryDelete(download);
            _log.LogWarning(ex, "Update failed");
            Stage = UpdateStage.Failed;
            Error = ex is UnauthorizedAccessException or IOException
                ? $"Couldn't replace the app in {Path.GetDirectoryName(exe)}. Move it to a folder you can write to, or download it from GitHub."
                : "Update failed: " + ex.Message;
            Raise();
        }
    }

    public void OpenReleasePage() => OpenPage(Available?.PageUrl ?? $"https://github.com/{UpdateChecker.Repository}/releases/latest");

    private void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _log.LogWarning("Could not open {Url}: {Message}", url, ex.Message); }
    }

    /// <summary>
    /// Runs first thing in a process started by <see cref="InstallAsync"/>: waits for the old process to exit so the
    /// single-instance lock is free, then removes the previous exe.
    /// </summary>
    public static void FinishPreviousUpdate(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, AfterUpdateArg, StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.WaitForExit(30_000);
            }
            catch (Exception)
            {
            }
        }
        if (Environment.ProcessPath is not { } exe) return;
        var old = exe + ".old";
        if (!File.Exists(old)) return;
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20 && File.Exists(old); attempt++)
            {
                TryDelete(old);
                await Task.Delay(1000);
            }
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
