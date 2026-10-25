using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Interop;
using System.Windows.Threading;
using Cs2DropChecker.Core;
using Cs2DropChecker.Core.Steam;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Cs2DropChecker.App.Services;

public sealed record BridgeResponse(int Status, string Url, string Body);

public enum SignInState { SignedIn, SignedOut, Unknown }

/// <summary>
/// Owns the WebView2 profile that holds the user's Steam login. Inventory requests run as same-origin fetches inside a
/// hidden page on steamcommunity.com, so the login cookie never leaves the browser.
/// </summary>
public sealed partial class SteamSession(Dispatcher ui, SteamProfileClient profiles, ILogger log)
{
    private const string BridgeUrl = "https://steamcommunity.com/cs2dc-bridge/";

    private Task<CoreWebView2Environment>? _env;
    private Task? _bridge;
    private CoreWebView2Controller? _controller;
    private HwndSource? _host;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> _pending = new();

    public bool IsSignedIn { get; private set; }
    public ulong SignedInSteamId { get; private set; }
    public event EventHandler? Changed;

    [GeneratedRegex(@"g_steamID\s*=\s*""(\d{17})""")] private static partial Regex PageSteamId();
    [GeneratedRegex(@"^/profiles/(\d{17})")] private static partial Regex ProfilePath();
    [GeneratedRegex(@"^/id/([^/]+)")] private static partial Regex VanityPath();

    public static string? RuntimeVersion
    {
        get
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Must be called on the UI thread.</summary>
    public Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_env is { IsFaulted: false }) return _env;
        return _env = CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDir, new CoreWebView2EnvironmentOptions { Language = "en-US" });
    }

    private Task EnsureBridgeAsync()
    {
        if (_bridge is { IsFaulted: false, IsCanceled: false }) return _bridge;
        return _bridge = CreateBridgeAsync();
    }

    private async Task CreateBridgeAsync()
    {
        var env = await GetEnvironmentAsync();
        _host ??= new HwndSource(new HwndSourceParameters("cs2dc-bridge") { Width = 640, Height = 480, WindowStyle = unchecked((int)0x80000000) });
        var controller = await env.CreateCoreWebView2ControllerAsync(_host.Handle);
        controller.IsVisible = false;
        var core = controller.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.AddWebResourceRequestedFilter(BridgeUrl + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!e.Request.Uri.StartsWith(BridgeUrl, StringComparison.Ordinal)) return;
            var html = "<!doctype html><meta charset=utf-8><title>cs2-drop-checker</title>"u8.ToArray();
            e.Response = env.CreateWebResourceResponse(new MemoryStream(html), 200, "OK", "Content-Type: text/html; charset=utf-8");
        };
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, e) =>
        {
            log.LogWarning("WebView2 process failed: {Kind}", e.ProcessFailedKind);
            _bridge = null;
            try { _controller?.Close(); } catch (Exception) { }
            _controller = null;
            foreach (var p in _pending.Values) p.TrySetResult(new BridgeResponse(-1, "", "browser process failed"));
        };

        var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= Done;
            nav.TrySetResult(e.IsSuccess);
        }
        core.NavigationCompleted += Done;
        core.Navigate(BridgeUrl);
        if (!await nav.Task)
        {
            controller.Close();
            throw new InvalidOperationException("The Steam bridge page did not load.");
        }
        _controller = controller;
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(BridgeUrl, StringComparison.Ordinal)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString();
            if (id is null || !_pending.TryRemove(id, out var tcs)) return;
            tcs.TrySetResult(new BridgeResponse(root.GetProperty("status").GetInt32(), root.GetProperty("url").GetString() ?? "", root.GetProperty("body").GetString() ?? ""));
        }
        catch (Exception ex)
        {
            log.LogWarning("Bad bridge message: {Message}", ex.Message);
        }
    }

    public Task<BridgeResponse> FetchAsync(string pathAndQuery, CancellationToken ct)
    {
        var script = $$"""
            const r = await fetch({{JsonSerializer.Serialize(pathAndQuery)}}, { credentials: 'include', cache: 'no-store' });
            """;
        return RunAsync(script, ct);
    }

    /// <summary>
    /// Posts a form the way Steam's own pages do: same origin, the sessionid cookie copied into the form, and the
    /// referrer Steam checks for market actions. <paramref name="sessionFallback"/> comes from g_sessionID on a page
    /// and is used only if the cookie is missing.
    /// </summary>
    public Task<BridgeResponse> PostFormAsync(string path, IReadOnlyList<KeyValuePair<string, string>> form, string referrer, string? sessionFallback, CancellationToken ct)
    {
        var fields = form.Select(kv => new[] { kv.Key, kv.Value }).ToArray();
        var script = $$"""
            let sid = (document.cookie.match(/(?:^|;\s*)sessionid=([^;]+)/) || [])[1] || {{JsonSerializer.Serialize(sessionFallback ?? "")}};
            if (!document.cookie.match(/(?:^|;\s*)sessionid=/) && sid) document.cookie = 'sessionid=' + sid + '; path=/; secure; samesite=none';
            if (!sid) throw new Error('no Steam session id');
            const form = new URLSearchParams();
            form.set('sessionid', decodeURIComponent(sid));
            for (const [k, v] of {{JsonSerializer.Serialize(fields)}}) form.set(k, v);
            const r = await fetch({{JsonSerializer.Serialize(path)}}, {
              method: 'POST', credentials: 'include', cache: 'no-store', body: form,
              referrer: {{JsonSerializer.Serialize(referrer)}}, referrerPolicy: 'unsafe-url',
              headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8', 'X-Requested-With': 'XMLHttpRequest' }
            });
            """;
        return RunAsync(script, ct);
    }

    /// <summary>Runs a fetch snippet that defines <c>r</c> inside the bridge page and returns its status, final URL and body.</summary>
    private async Task<BridgeResponse> RunAsync(string fetchSnippet, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        try
        {
            await await ui.InvokeAsync(EnsureBridgeAsync);
            var tcs = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            var script = $$"""
                (async () => {
                  const id = {{JsonSerializer.Serialize(id)}};
                  try {
                    {{fetchSnippet}}
                    const body = await r.text();
                    chrome.webview.postMessage(JSON.stringify({ id, status: r.status, url: r.url, body }));
                  } catch (e) {
                    chrome.webview.postMessage(JSON.stringify({ id, status: -1, url: '', body: String(e) }));
                  }
                })();
                """;
            await await ui.InvokeAsync(() => _controller!.CoreWebView2.ExecuteScriptAsync(script));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await using var reg = timeout.Token.Register(() => tcs.TrySetResult(new BridgeResponse(-1, "", "timeout")));
            var result = await tcs.Task;
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Bridge fetch failed: {Message}", ex.Message);
            return new BridgeResponse(-1, "", ex.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Asks Steam who is signed in inside the built-in browser. Never reads the cookie itself.</summary>
    public async Task<SignInState> CheckSignedInAsync(CancellationToken ct)
    {
        var r = await FetchAsync("/my/", ct);
        if (r.Status < 0 || r.Status >= 500) return SignInState.Unknown;
        ulong id = 0;
        var page = PageSteamId().Match(r.Body);
        if (page.Success) ulong.TryParse(page.Groups[1].Value, out id);
        if (id == 0 && Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) && !uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
        {
            var m = ProfilePath().Match(uri.AbsolutePath);
            if (m.Success) ulong.TryParse(m.Groups[1].Value, out id);
            else if (VanityPath().Match(uri.AbsolutePath) is { Success: true } v)
            {
                try { id = (await profiles.ResolveAsync("https://steamcommunity.com/id/" + v.Groups[1].Value, ct)).SteamId; }
                catch (SteamLookupException) { }
            }
        }
        SetState(id != 0, id);
        return id != 0 ? SignInState.SignedIn : SignInState.SignedOut;
    }

    public void MarkSignedOut() => SetState(false, 0);

    public async Task SignOutAsync()
    {
        try
        {
            await await ui.InvokeAsync(async () =>
            {
                await EnsureBridgeAsync();
                var core = _controller!.CoreWebView2;
                core.CookieManager.DeleteAllCookies();
                await core.Profile.ClearBrowsingDataAsync();
            });
        }
        catch (Exception ex)
        {
            log.LogWarning("Sign-out cleanup failed: {Message}", ex.Message);
        }
        SetState(false, 0);
    }

    private void SetState(bool signedIn, ulong id)
    {
        var changed = signedIn != IsSignedIn || id != SignedInSteamId;
        IsSignedIn = signedIn;
        SignedInSteamId = id;
        if (changed) ui.BeginInvoke(() => Changed?.Invoke(this, EventArgs.Empty));
    }
}

public sealed class WebViewInventoryTransport(SteamSession session) : IInventoryTransport
{
    public bool IsSignedIn => true;

    public async Task<TransportResponse> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var r = await session.FetchAsync(pathAndQuery, ct);
        return new TransportResponse(r.Status, r.Body);
    }
}
