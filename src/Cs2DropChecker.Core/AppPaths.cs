namespace Cs2DropChecker.Core;

public static class AppPaths
{
    public const string AppFolderName = "CS2DropChecker";

    /// <summary>Set CS2DC_HOME to keep all data in one folder (portable or test runs).</summary>
    public static string? HomeOverride => Environment.GetEnvironmentVariable("CS2DC_HOME") is { Length: > 0 } h ? h : null;

    public static string RoamingDir => Ensure(HomeOverride is { } h
        ? Path.Combine(h, "roaming")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName));

    public static string LocalDir => Ensure(HomeOverride is { } h
        ? Path.Combine(h, "local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName));

    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");
    public static string DatabaseFile => Path.Combine(LocalDir, "data.db");
    public static string ImagesDir => Ensure(Path.Combine(LocalDir, "images"));
    public static string CacheDir => Ensure(Path.Combine(LocalDir, "cache"));
    public static string LogsDir => Ensure(Path.Combine(LocalDir, "logs"));
    public static string WebViewDir => Ensure(Path.Combine(LocalDir, "WebView2"));

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
