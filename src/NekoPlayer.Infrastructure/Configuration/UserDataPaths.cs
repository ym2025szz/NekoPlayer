using NekoPlayer.Core.Interfaces;

namespace NekoPlayer.Infrastructure.Configuration;

public sealed record UserDataPathInputs(
    bool IsWindows,
    string LocalApplicationData,
    string HomeDirectory,
    string? OverrideRoot = null,
    string? XdgDataHome = null,
    string? XdgConfigHome = null,
    string? XdgCacheHome = null);

public sealed record UserDataPathLayout(
    string Root,
    string DataDirectory,
    string DatabasePath,
    string LogsDirectory,
    string CoversDirectory,
    string LyricsDirectory,
    string ConfigDirectory,
    string TempDirectory,
    string SettingsPath,
    string CacheRoot);

public sealed class UserDataPaths : IUserDataPaths
{
    public const string DataRootOverrideEnvironmentVariable = "NEKOPLAYER_DATA_ROOT";

    public UserDataPaths()
        : this(CreateCurrentInputs())
    {
    }

    public UserDataPaths(UserDataPathInputs inputs)
    {
        var layout = Resolve(inputs);
        Root = layout.Root;
        DataDirectory = layout.DataDirectory;
        DatabasePath = layout.DatabasePath;
        LogsDirectory = layout.LogsDirectory;
        CoversDirectory = layout.CoversDirectory;
        LyricsDirectory = layout.LyricsDirectory;
        ConfigDirectory = layout.ConfigDirectory;
        TempDirectory = layout.TempDirectory;
        SettingsPath = layout.SettingsPath;
        CacheRoot = layout.CacheRoot;
    }

    public string Root { get; }
    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string LogsDirectory { get; }
    public string CoversDirectory { get; }
    public string LyricsDirectory { get; }
    public string ConfigDirectory { get; }
    public string TempDirectory { get; }
    public string SettingsPath { get; }
    public string CacheRoot { get; }

    public void EnsureCreated()
    {
        foreach (var path in new[] { Root, DataDirectory, LogsDirectory, CoversDirectory, LyricsDirectory, ConfigDirectory, TempDirectory, CacheRoot })
            Directory.CreateDirectory(path);
    }

    public static UserDataPathLayout Resolve(UserDataPathInputs inputs)
    {
        var overrideRoot = inputs.OverrideRoot;
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            var root = Path.GetFullPath(overrideRoot);
            return CreateLegacyLayout(root);
        }

        if (inputs.IsWindows)
        {
            if (string.IsNullOrWhiteSpace(inputs.LocalApplicationData))
                throw new InvalidOperationException("Windows LocalApplicationData directory is unavailable.");
            return CreateLegacyLayout(Path.Combine(inputs.LocalApplicationData, "NekoPlayer"));
        }

        if (string.IsNullOrWhiteSpace(inputs.HomeDirectory))
            throw new InvalidOperationException("Linux home directory is unavailable.");

        var dataHome = ResolveXdgHome(inputs.XdgDataHome, inputs.HomeDirectory, ".local", "share");
        var configHome = ResolveXdgHome(inputs.XdgConfigHome, inputs.HomeDirectory, ".config");
        var cacheHome = ResolveXdgHome(inputs.XdgCacheHome, inputs.HomeDirectory, ".cache");
        var dataRoot = Path.Combine(dataHome, "NekoPlayer");
        var configRoot = Path.Combine(configHome, "NekoPlayer");
        var cacheRoot = Path.Combine(cacheHome, "NekoPlayer");

        return new UserDataPathLayout(
            dataRoot,
            dataRoot,
            Path.Combine(dataRoot, "nekoplayer.db"),
            Path.Combine(cacheRoot, "Logs"),
            Path.Combine(cacheRoot, "Covers"),
            Path.Combine(cacheRoot, "Lyrics"),
            configRoot,
            Path.Combine(cacheRoot, "Temp"),
            Path.Combine(configRoot, "settings.json"),
            cacheRoot);
    }

    private static UserDataPathInputs CreateCurrentInputs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new UserDataPathInputs(
            OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            home,
            Environment.GetEnvironmentVariable(DataRootOverrideEnvironmentVariable),
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME"));
    }

    private static UserDataPathLayout CreateLegacyLayout(string root)
    {
        var data = Path.Combine(root, "Data");
        var cache = Path.Combine(root, "Cache");
        var config = Path.Combine(root, "Config");
        return new UserDataPathLayout(
            root,
            data,
            Path.Combine(data, "nekoplayer.db"),
            Path.Combine(root, "Logs"),
            Path.Combine(cache, "Covers"),
            Path.Combine(cache, "Lyrics"),
            config,
            Path.Combine(root, "Temp"),
            Path.Combine(config, "settings.json"),
            cache);
    }

    private static string ResolveXdgHome(string? configured, string home, params string[] fallbackSegments)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return Path.Combine(new[] { home }.Concat(fallbackSegments).ToArray());
    }
}
