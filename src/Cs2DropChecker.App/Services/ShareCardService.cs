using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cs2DropChecker.App.Controls;
using Cs2DropChecker.App.ViewModels;
using Cs2DropChecker.App.Views;
using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Microsoft.Extensions.Logging;

namespace Cs2DropChecker.App.Services;

public sealed record ShareResult(bool Ok, string Message, string? Path = null);

/// <summary>Everything a share card shows. Images are decoded before rendering so the PNG never has blanks.</summary>
public sealed class ShareCardModel
{
    public BitmapSource? Image { get; init; }
    public BitmapSource? Avatar { get; init; }
    public string RarityHex { get; init; } = "b0c3d9";
    public Brush RarityBrush => BrushCache.FromHex(RarityHex);
    public Brush Glow => BrushCache.Tint(RarityHex, 0x90);
    public string RarityLine { get; init; } = "";
    public string Weapon { get; init; } = "";
    public string Skin { get; init; } = "";
    public string Wear { get; init; } = "";
    public string Price { get; init; } = "";
    public string How { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public bool ShowProfile { get; init; }
    public bool Gold { get; init; }
}

/// <summary>Renders a drop as a PNG, copies it to the clipboard and saves it locally. Nothing is uploaded.</summary>
public sealed class ShareCardService(AppServices s, ILogger log)
{
    public const double Width = 640;
    public const double Height = 360;

    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CS2 Drop Checker");

    public string Folder => s.Settings.Share.Folder.Length > 0 ? s.Settings.Share.Folder : DefaultFolder;

    public async Task<ShareResult> ShareLastDropAsync()
    {
        var id = s.Settings.SteamId64;
        if (id == 0) return new(false, "No profile set up yet");
        DropRecord? last;
        try { last = s.Db.GetDrops(id, 1).FirstOrDefault(); }
        catch (Exception) { last = null; }
        if (last is null) return new(false, "No drops to share yet");
        return await ShareAsync(last);
    }

    public async Task<ShareResult> ShareAsync(DropRecord d)
    {
        var price = d.PriceAtDrop is { } p ? Money.Format(p, d.Currency) : s.Prices.GetCached(d.Item.MarketHashName) is { } q ? Money.Format(q) : "No price";
        var how = $"{ArrivalKindText.Label(d.Kind)} · {d.SeenAt.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";
        return await ShareAsync(d.Item, price, how);
    }

    public async Task<ShareResult> ShareAsync(InventoryItem item, string price, string how)
    {
        try
        {
            var settings = s.Settings;
            var show = settings.Share.ShowProfile && settings.ProfileName.Length > 0;
            var image = await LoadAsync(item.IconUrl, 600);
            var avatar = show && settings.AvatarUrl.Length > 0 ? await LoadAsync(settings.AvatarUrl, 96) : null;
            var model = new ShareCardModel
            {
                Image = image,
                Avatar = avatar,
                RarityHex = item.RarityHex,
                RarityLine = item.RarityLine,
                Weapon = item.WeaponTitle,
                Skin = item.SkinTitle,
                Wear = item.HasWear || item.Float.HasValue ? ItemText.WearLong(item) : "",
                Price = price,
                How = how,
                ProfileName = settings.ProfileName,
                ShowProfile = show,
                Gold = item.IsStar,
            };
            var bitmap = Render(model);
            var copied = false;
            if (settings.Share.CopyToClipboard) copied = TryCopy(bitmap);
            var path = Save(bitmap, item.Name);
            var where = System.IO.Path.GetFileName(path);
            return new(true, copied ? $"Share card copied · saved as {where}" : $"Share card saved as {where}", path);
        }
        catch (Exception ex)
        {
            log.LogWarning("Share card failed: {Message}", ex.Message);
            return new(false, "Share card failed: " + ex.Message);
        }
    }

    private async Task<BitmapSource?> LoadAsync(string icon, int width)
    {
        if (string.IsNullOrEmpty(icon)) return null;
        var bytes = await s.Images.GetBytesAsync(icon);
        if (bytes is null) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = width;
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

    public static BitmapSource Render(ShareCardModel model)
    {
        var card = new ShareCard { DataContext = model, Width = Width, Height = Height };
        card.Measure(new Size(Width, Height));
        card.Arrange(new Rect(0, 0, Width, Height));
        card.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)(Width * 2), (int)(Height * 2), 192, 192, PixelFormats.Pbgra32);
        rtb.Render(card);
        rtb.Freeze();
        return rtb;
    }

    private bool TryCopy(BitmapSource bitmap)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetImage(bitmap);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }
        log.LogInformation("Clipboard was busy; the share card was only saved");
        return false;
    }

    private string Save(BitmapSource bitmap, string name)
    {
        var folder = Folder;
        Directory.CreateDirectory(folder);
        var safe = new string(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c == '★' || c == '|' ? ' ' : c).ToArray());
        safe = string.Join(" ", safe.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (safe.Length > 60) safe = safe[..60].Trim();
        if (safe.Length == 0) safe = "drop";
        var path = System.IO.Path.Combine(folder, $"{safe} {DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture)}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var f = File.Create(path)) encoder.Save(f);
        return path;
    }
}
