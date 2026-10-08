using NekoPlayer.Core.Interfaces;

namespace NekoPlayer.Infrastructure.Configuration;

public sealed class UserDataPaths : IUserDataPaths
{
    public const string DataRootOverrideEnvironmentVariable = "NEKOPLAYER_DATA_ROOT";

    public UserDataPaths()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(DataRootOverrideEnvironmentVariable);
        Root = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NekoPlayer")
            : Path.GetFullPath(overrideRoot);
        DataDirectory = Path.Combine(Root, "Data");
        DatabasePath = Path.Combine(DataDirectory, "nekoplayer.db");
        LogsDirectory = Path.Combine(Root, "Logs");
        CoversDirectory = Path.Combine(Root, "Cache", "Covers");
        LyricsDirectory = Path.Combine(Root, "Cache", "Lyrics");
        ConfigDirectory = Path.Combine(Root, "Config");
        TempDirectory = Path.Combine(Root, "Temp");
        SettingsPath = Path.Combine(ConfigDirectory, "settings.json");
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

    public void EnsureCreated()
    {
        foreach (var path in new[] { Root, DataDirectory, LogsDirectory, CoversDirectory, LyricsDirectory, ConfigDirectory, TempDirectory })
            Directory.CreateDirectory(path);
    }
}
