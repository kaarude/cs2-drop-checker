using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Services;

/// <summary>Downloads Steam item icons once to disk and keeps a bounded set of decoded bitmaps in memory.</summary>
public sealed class ImageCache(HttpClient http, string dir, ILogger log)
{
    public const string Cdn = "https://community.cloudflare.steamstatic.com/economy/image/";
    private const int Capacity = 700;

    public static ImageCache? Instance { get; set; }

    private readonly SemaphoreSlim _downloads = new(6);
    private readonly object _gate = new();
    private readonly Dictionary<(string, int), LinkedListNode<((string, int) Key, BitmapSource Image)>> _map = new();
    private readonly LinkedList<((string, int) Key, BitmapSource Image)> _lru = new();
    private readonly ConcurrentDictionary<(string, int), Task<BitmapSource?>> _inflight = new();

    public BitmapSource? TryGetMemory(string icon, int width)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue((icon, width), out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }
    }

    public Task<BitmapSource?> GetAsync(string icon, int width)
    {
        if (TryGetMemory(icon, width) is { } hit) return Task.FromResult<BitmapSource?>(hit);
        return _inflight.GetOrAdd((icon, width), LoadAsync);
    }

    private async Task<BitmapSource?> LoadAsync((string Icon, int Width) key)
    {
        await Task.Yield();
        try
        {
            var bytes = await GetBytesAsync(key.Icon);
            if (bytes is null) return null;
            var image = await Task.Run(() => Decode(bytes, key.Width));
            if (image is not null) Remember(key, image);
            return image;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public static string UrlFor(string icon) =>
        icon.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? icon : Cdn + icon + "/360fx360f";

    public async Task<byte[]?> GetBytesAsync(string icon)
    {
        var url = UrlFor(icon);
        var file = Path.Combine(dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".img");
        if (File.Exists(file))
        {
            try { return await File.ReadAllBytesAsync(file); }
            catch (IOException) { }
        }
        await _downloads.WaitAsync();
        try
        {
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 64) return null;
            var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, file, overwrite: true);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            log.LogDebug("Image download failed: {Message}", ex.Message);
            return null;
        }
        finally
        {
            _downloads.Release();
        }
    }

    private static BitmapSource? Decode(byte[] bytes, int width)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width > 0) bmp.DecodePixelWidth = width;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Remember((string, int) key, BitmapSource image)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing)) _lru.Remove(existing);
            var node = _lru.AddFirst((key, image));
            _map[key] = node;
            while (_lru.Count > Capacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
