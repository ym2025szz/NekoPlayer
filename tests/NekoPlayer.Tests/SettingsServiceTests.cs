using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Configuration;

namespace NekoPlayer.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly TestPaths _paths = new();

    [Fact]
    public async Task RoundTripsSettings()
    {
        var service = new JsonSettingsService(_paths);
        var expected = new AppSettings { Volume = 0.42f, PlayMode = PlayMode.Shuffle, LastPosition = TimeSpan.FromSeconds(12), SpectrumFps = 24 };
        await service.SaveAsync(expected);
        var actual = await service.LoadAsync();
        Assert.Equal(expected.Volume, actual.Volume);
        Assert.Equal(PlayMode.Shuffle, actual.PlayMode);
        Assert.Equal(TimeSpan.FromSeconds(12), actual.LastPosition);
    }

    [Fact]
    public async Task CorruptedConfigFallsBackToDefaults()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        await File.WriteAllTextAsync(_paths.SettingsPath, "{oops");
        var actual = await new JsonSettingsService(_paths).LoadAsync();
        Assert.Equal(0.75f, actual.Volume);
        Assert.Equal(PlayMode.RepeatAll, actual.PlayMode);
    }

    public void Dispose() { if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true); }

    private sealed class TestPaths : IUserDataPaths
    {
        public TestPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), "NekoPlayerTests", Guid.NewGuid().ToString("N"));
            DataDirectory = Path.Combine(Root, "Data"); LogsDirectory = Path.Combine(Root, "Logs"); CoversDirectory = Path.Combine(Root, "Covers");
            LyricsDirectory = Path.Combine(Root, "Lyrics"); ConfigDirectory = Path.Combine(Root, "Config"); TempDirectory = Path.Combine(Root, "Temp");
            DatabasePath = Path.Combine(DataDirectory, "test.db"); SettingsPath = Path.Combine(ConfigDirectory, "settings.json"); EnsureCreated();
        }
        public string Root { get; } public string DataDirectory { get; } public string DatabasePath { get; } public string LogsDirectory { get; }
        public string CoversDirectory { get; } public string LyricsDirectory { get; } public string ConfigDirectory { get; } public string TempDirectory { get; } public string SettingsPath { get; }
        public void EnsureCreated() { foreach (var path in new[] { Root, DataDirectory, LogsDirectory, CoversDirectory, LyricsDirectory, ConfigDirectory, TempDirectory }) Directory.CreateDirectory(path); }
    }
}
