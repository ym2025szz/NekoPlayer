using System.Diagnostics;
using NekoPlayer.App;
using NekoPlayer.Audio.Playback;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Infrastructure.Configuration;

namespace NekoPlayer.Tests;

public sealed class UserDataPathCrossPlatformTests
{
    [Fact]
    public void WindowsLayoutKeepsLegacyDatabaseLocation()
    {
        var local = Path.Combine(Path.GetTempPath(), "windows-local-appdata");
        var layout = UserDataPaths.Resolve(new UserDataPathInputs(true, local, Path.GetTempPath()));
        Assert.Equal(Path.Combine(local, "NekoPlayer"), layout.Root);
        Assert.Equal(Path.Combine(local, "NekoPlayer", "Data", "nekoplayer.db"), layout.DatabasePath);
    }

    [Fact]
    public void LinuxUsesConfiguredXdgRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var layout = UserDataPaths.Resolve(new UserDataPathInputs(false, string.Empty, Path.Combine(root, "home"),
            XdgDataHome: Path.Combine(root, "data"), XdgConfigHome: Path.Combine(root, "config"), XdgCacheHome: Path.Combine(root, "cache")));
        Assert.Equal(Path.Combine(root, "data", "NekoPlayer"), layout.DataDirectory);
        Assert.Equal(Path.Combine(root, "config", "NekoPlayer", "settings.json"), layout.SettingsPath);
        Assert.Equal(Path.Combine(root, "cache", "NekoPlayer", "Covers"), layout.CoversDirectory);
    }

    [Fact]
    public void LinuxFallsBackToStandardHomeDirectories()
    {
        var home = Path.Combine(Path.GetTempPath(), "linux-home");
        var layout = UserDataPaths.Resolve(new UserDataPathInputs(false, string.Empty, home));
        Assert.Equal(Path.Combine(home, ".local", "share", "NekoPlayer", "nekoplayer.db"), layout.DatabasePath);
        Assert.Equal(Path.Combine(home, ".config", "NekoPlayer"), layout.ConfigDirectory);
        Assert.Equal(Path.Combine(home, ".cache", "NekoPlayer"), layout.CacheRoot);
    }

    [Fact]
    public void ExplicitOverrideKeepsAllDataIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var layout = UserDataPaths.Resolve(new UserDataPathInputs(false, string.Empty, "/unused", OverrideRoot: root));
        Assert.All(new[] { layout.DatabasePath, layout.SettingsPath, layout.LogsDirectory, layout.CoversDirectory, layout.TempDirectory },
            path => Assert.StartsWith(Path.GetFullPath(root), path, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class FfmpegCrossPlatformTests
{
    [Fact]
    public void ExecutableNamesMatchPlatform()
    {
        Assert.Equal(("ffmpeg.exe", "ffprobe.exe"), FfmpegLocator.GetExecutableNames(true));
        Assert.Equal(("ffmpeg", "ffprobe"), FfmpegLocator.GetExecutableNames(false));
    }

    [Fact]
    public void LinuxPathLookupUsesExtensionlessLowercaseNames()
    {
        var root = Directory.CreateTempSubdirectory("nekoplayer-ffmpeg-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "ffmpeg"), string.Empty);
            File.WriteAllText(Path.Combine(root, "ffprobe"), string.Empty);
            Assert.Equal(Path.GetFullPath(root), FfmpegLocator.FindBinaryDirectory(false, null, root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LinuxLookupDoesNotTreatUppercaseFilesAsValid()
    {
        var root = Directory.CreateTempSubdirectory("nekoplayer-case-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "FFMPEG"), string.Empty);
            File.WriteAllText(Path.Combine(root, "FFPROBE"), string.Empty);
            Assert.NotEqual(Path.GetFullPath(root), FfmpegLocator.FindBinaryDirectory(false, null, root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LinuxNullOutputArgumentsAreSafeForSpaces()
    {
        const string inputPath = "/tmp/音乐 files/test song.wav";
        var info = LinuxFfmpegAudioPlayerService.CreateProcessStartInfo("/usr/bin/ffmpeg", inputPath, TimeSpan.FromSeconds(12.5), 0.4f, "null");
        Assert.Equal("/usr/bin/ffmpeg", info.FileName);
        Assert.Contains(Path.GetFullPath(inputPath), info.ArgumentList);
        Assert.Contains("null", info.ArgumentList);
        Assert.DoesNotContain("ffmpeg.exe", info.FileName, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PlatformLauncherTests
{
    [Fact]
    public void WindowsFolderLaunchUsesExplorerArgumentList()
    {
        var path = Path.Combine(Path.GetTempPath(), "folder with spaces");
        var info = PlatformLauncher.CreateOpenDirectoryStartInfo(path, true);
        Assert.Equal("explorer.exe", info.FileName);
        Assert.Equal(Path.GetFullPath(path), Assert.Single(info.ArgumentList));
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void LinuxFolderLaunchUsesXdgOpenArgumentList()
    {
        var path = Path.Combine(Path.GetTempPath(), "folder with spaces");
        var info = PlatformLauncher.CreateOpenDirectoryStartInfo(path, false);
        Assert.Equal("xdg-open", info.FileName);
        Assert.Equal(Path.GetFullPath(path), Assert.Single(info.ArgumentList));
        Assert.False(info.UseShellExecute);
    }
}

public sealed class AudioFactoryCrossPlatformTests
{
    [Fact]
    public async Task FactorySelectsWindowsAndLinuxBackends()
    {
        var locator = new FakeLocator();
        var spectrum = new FakeSpectrum();
        await using var windows = AudioPlayerFactory.CreateForPlatform(true, locator, spectrum);
        await using var linux = AudioPlayerFactory.CreateForPlatform(false, locator, spectrum);
        Assert.IsType<FfmpegAudioPlayerService>(windows);
        Assert.IsType<LinuxFfmpegAudioPlayerService>(linux);
    }

    private sealed class FakeLocator : IFfmpegLocator
    {
        public string BinaryDirectory => Path.GetTempPath();
        public string FfmpegPath => Path.Combine(BinaryDirectory, "ffmpeg");
        public string FfprobePath => Path.Combine(BinaryDirectory, "ffprobe");
        public bool IsAvailable => false;
        public bool HasSharedLibraries => true;
        public string Version => string.Empty;
        public string StatusMessage => string.Empty;
        public void Configure() { }
        public Task<FfmpegValidationResult> ValidateAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FfmpegValidationResult(false, true, string.Empty, BinaryDirectory, string.Empty));
    }

    private sealed class FakeSpectrum : ISpectrumService
    {
        public IReadOnlyList<float> Bands => [];
        public bool IsEnabled { get; set; }
        public int FramesPerSecond { get; set; }
        public event EventHandler<IReadOnlyList<float>>? SpectrumUpdated { add { } remove { } }
        public void PushPcm(ReadOnlySpan<byte> float32StereoPcm) { }
    }
}
