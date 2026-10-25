using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cs2DropChecker.Core.Util;

public static class HttpFactory
{
    // A browser-like agent without a Steam session gets HTTP 429 from /inventory almost immediately; a plain tool agent does not.
    public static readonly string UserAgent = $"cs2-drop-checker/{Updates.AppVersion.Text} (+https://github.com/kaarude/cs2-drop-checker)";

    public static HttpClient Create(DecompressionMethods decompression = DecompressionMethods.All, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = decompression,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return http;
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}

public static class SteamIdMask
{
    public static string Mask(ulong id) => Mask(id.ToString());

    public static string Mask(string id) => id.Length < 12 ? id : id[..8] + "••••••" + id[^2..];
}

public static class TimeText
{
    public static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null) return "never";
        var span = now - at.Value;
        if (span < TimeSpan.FromSeconds(5)) return "just now";
        if (span < TimeSpan.FromMinutes(1)) return $"{(int)span.TotalSeconds} s ago";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} d ago";
    }
}
