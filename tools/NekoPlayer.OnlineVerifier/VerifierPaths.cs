using NekoPlayer.Core.Interfaces;

namespace NekoPlayer.OnlineVerifier;

/// <summary>Uses a fresh isolated root; never reads account state in the application data root.</summary>
internal sealed class VerifierPaths : IUserDataPaths, IDisposable
{
    private readonly string _parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NekoPlayerOnlineVerifier"));
    public VerifierPaths()
    {
        Root = Path.GetFullPath(Path.Combine(_parent, Guid.NewGuid().ToString("N")));
        DataDirectory = Path.Combine(Root, "Data");
        DatabasePath = Path.Combine(DataDirectory, "verifier.db");
        LogsDirectory = Path.Combine(Root, "Logs");
        CoversDirectory = Path.Combine(Root, "Covers");
        LyricsDirectory = Path.Combine(Root, "Lyrics");
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
    public void Dispose()
    {
        var resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(_parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Verifier cleanup path left the isolated workspace.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
