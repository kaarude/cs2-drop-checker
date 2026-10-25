using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cs2DropChecker.Core.Updates;

public static class AppVersion
{
    public static Version Current { get; } =
        Parse(typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0);

    public static string Text => Format(Current);

    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    /// <summary>Accepts "v0.2.0", "0.2", "0.2.0-beta" or "0.2.0+sha". Pre-release and build suffixes are ignored.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }
}

public sealed record UpdateInfo(Version Version, string Tag, string Title, string Notes, string PageUrl, string DownloadUrl, long Size, string? Sha256)
{
    public string VersionText => AppVersion.Format(Version);
}

/// <summary>Looks for a newer GitHub release that ships the single-file exe.</summary>
public sealed class UpdateChecker(HttpClient http, string repository = UpdateChecker.Repository)
{
    public const string Repository = "kaarude/cs2-drop-checker";
    public const string AssetName = "cs2-drop-checker.exe";

    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return ParseLatest(await resp.Content.ReadAsStringAsync(ct), current);
    }

    /// <param name="allowAnyUrl">Only for a local test feed; real releases must download from github.com.</param>
    public static UpdateInfo? ParseLatest(string json, Version current, bool allowAnyUrl = false)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        var tag = Str(r, "tag_name") ?? "";
        if (AppVersion.Parse(tag) is not { } version || version <= current) return null;
        if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (!string.Equals(Str(a, "name"), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var url = Str(a, "browser_download_url");
            if (url is null || (!allowAnyUrl && !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))) continue;
            var size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0;
            var digest = Str(a, "digest");
            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
            return new UpdateInfo(version, tag, Str(r, "name") ?? tag, Str(r, "body") ?? "", Str(r, "html_url") ?? $"https://github.com/{Repository}/releases", url, size, sha);
        }
        return null;
    }

    /// <summary>Downloads the new exe and checks its size and SHA-256 when GitHub publishes one. Deletes the file on failure.</summary>
    public async Task DownloadAsync(UpdateInfo info, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var uri = new Uri(info.DownloadUrl);
            using var resp = uri.IsFile ? null : await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            resp?.EnsureSuccessStatusCode();
            var total = resp?.Content.Headers.ContentLength ?? info.Size;
            await using (var src = resp is null ? File.OpenRead(uri.LocalPath) : await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
                }
                if (info.Size > 0 && done != info.Size)
                    throw new InvalidDataException($"The download was {done} bytes, expected {info.Size}.");
            }
            var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (info.Sha256 is { } expected && hash != expected)
                throw new InvalidDataException("The download's checksum didn't match the release.");
        }
        catch
        {
            try { File.Delete(destination); } catch (Exception) { }
            throw;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
