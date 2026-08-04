using System.Security.Cryptography;
using Avalonia;
using Microsoft.EntityFrameworkCore;
using NekoPlayer.App.Controls;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Tests;

public sealed class TrackCoverProviderTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyCoverPathReturnsDefault(string? path)
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var provider = Provider();
        var result = await provider.GetCoverAsync(path, 80);
        Assert.True(result.IsDefault);
        Assert.Same(provider.DefaultCover, result.Image);
        provider.DefaultCover?.Dispose();
    }

    [Fact]
    public async Task MissingCoverFileReturnsDefault()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var provider = Provider();
        var result = await provider.GetCoverAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png"), 80);
        Assert.True(result.IsDefault);
        Assert.Same(provider.DefaultCover, result.Image);
        provider.DefaultCover?.Dispose();
    }

    [Fact]
    public async Task CorruptCoverReturnsDefault()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var path = TempFile([1, 2, 3, 4]);
        try
        {
            var provider = Provider();
            var result = await provider.GetCoverAsync(path, 80);
            Assert.True(result.IsDefault);
            Assert.Same(provider.DefaultCover, result.Image);
            provider.DefaultCover?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ValidCoverHasPriorityOverDefault()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var path = TempFile(TinyPng);
        try
        {
            var provider = Provider();
            var result = await provider.GetCoverAsync(path, 80);
            Assert.False(result.IsDefault);
            Assert.NotNull(result.Image);
            Assert.NotSame(provider.DefaultCover, result.Image);
            result.Image?.Dispose();
            provider.DefaultCover?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact] public void DefaultCoverUriUsesEmbeddedAppIcon() => Assert.Equal("avares://NekoPlayer/Assets/AppIcon.png", TrackCoverProvider.DefaultCoverUri);

    [Fact]
    public async Task MultipleMissingTracksShareOneDefaultBitmap()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var provider = Provider();
        var first = await provider.GetCoverAsync(null, 80);
        var second = await provider.GetCoverAsync(string.Empty, 520);
        Assert.Same(first.Image, second.Image);
        provider.DefaultCover?.Dispose();
    }

    [Fact]
    public void DefaultBitmapDecodesOnlyOnce()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var provider = Provider();
        _ = provider.DefaultCover;
        _ = provider.DefaultCover;
        Assert.Equal(1, provider.DefaultDecodeCount);
        provider.DefaultCover?.Dispose();
    }

    [Fact]
    public async Task CorruptCoverBytesAreNotModified()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var path = TempFile([9, 8, 7, 6]);
        try
        {
            var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var provider = Provider();
            _ = await provider.GetCoverAsync(path, 80);
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            provider.DefaultCover?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ValidCoverFileIsReadOnly()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var path = TempFile(TinyPng);
        try
        {
            var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var provider = Provider();
            var result = await provider.GetCoverAsync(path, 80);
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            result.Image?.Dispose();
            provider.DefaultCover?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DefaultFallbackDoesNotWriteTrackCoverPath()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var track = new Track { CoverCachePath = null };
        var provider = Provider();
        _ = await provider.GetCoverAsync(track.CoverCachePath, 80);
        Assert.Null(track.CoverCachePath);
        provider.DefaultCover?.Dispose();
    }

    [Fact]
    public async Task StaleCoverPathRemainsRealSourceMetadata()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var stale = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        var track = new Track { CoverCachePath = stale };
        var provider = Provider();
        _ = await provider.GetCoverAsync(track.CoverCachePath, 80);
        Assert.Equal(stale, track.CoverCachePath);
        provider.DefaultCover?.Dispose();
    }

    [Fact]
    public async Task MissingCoverFallbackCreatesNoCacheFile()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        var provider = Provider();
        _ = await provider.GetCoverAsync(path, 80);
        Assert.False(File.Exists(path));
        provider.DefaultCover?.Dispose();
    }

    [Fact]
    public async Task DecodeFailureIsRecoverable()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var path = TempFile([0xFF, 0xD8, 0x00]);
        try
        {
            var provider = Provider();
            var exception = await Record.ExceptionAsync(() => provider.GetCoverAsync(path, 80));
            Assert.Null(exception);
            provider.DefaultCover?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AppIconCanBeDecodedAsDefaultCover()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        var icon = Path.Combine(SourceContracts.Root, "src", "NekoPlayer.App", "Assets", "AppIcon.png");
        var provider = new TrackCoverProvider(() => File.OpenRead(icon));
        Assert.NotNull(provider.DefaultCover);
        Assert.Equal(1, provider.DefaultDecodeCount);
        provider.DefaultCover?.Dispose();
    }

    private static TrackCoverProvider Provider() => new(() => new MemoryStream(TinyPng, writable: false));
    private static string TempFile(byte[] bytes) { var path = Path.Combine(Path.GetTempPath(), "NekoCover-" + Guid.NewGuid().ToString("N") + ".png"); File.WriteAllBytes(path, bytes); return path; }
}

public sealed class FavoriteVisualContractTests
{
    private static string ButtonSource => SourceContracts.Read(@"src\NekoPlayer.App\Controls\FavoriteButton.cs");
    private static string WindowSource => SourceContracts.Read(@"src\NekoPlayer.App\Views\MainWindow.axaml");

    [Fact] public void UncheckedColorIsGrayCyan() => Assert.Equal("#8FAEB2", FavoriteButton.UncheckedColor);
    [Fact] public void CheckedColorIsPink() => Assert.Equal("#FF5D7D", FavoriteButton.CheckedColor);
    [Fact] public void OutlineAndFilledGeometryAreDifferent() => Assert.NotEqual(FavoriteButton.OutlineGeometryData, FavoriteButton.FilledGeometryData);
    [Fact] public void UncheckedUsesOutlineStroke() => Assert.Contains("_outlinePath.Stroke = foreground", ButtonSource);
    [Fact] public void CheckedUsesFilledPath() => Assert.Contains("_filledPath.Fill = foreground", ButtonSource);
    [Fact] public void UncheckedTooltipIsExact() => Assert.Equal("添加到喜欢", FavoriteButton.AddTooltip);
    [Fact] public void CheckedTooltipIsExact() => Assert.Equal("取消喜欢", FavoriteButton.RemoveTooltip);
    [Fact] public void AutomationNameTracksState() => Assert.Contains("AutomationProperties.SetName(this, accessibleText)", ButtonSource);
    [Fact] public void FormalControlDoesNotUseUnicodeHearts() { Assert.DoesNotContain("♡", ButtonSource); Assert.DoesNotContain("♥", ButtonSource); }
    [Fact] public void HoverKeepsDistinctColors() { Assert.Contains("#C4D7D9", ButtonSource); Assert.Contains("#FF7892", ButtonSource); }
    [Fact] public void CheckedStateHasLightPinkBackground() => Assert.Contains("#22FF5D7D", ButtonSource);
    [Fact] public void DisabledStatePreservesVisibleState() => Assert.Contains("!IsEnabled ? 0.58", ButtonSource);
    [Fact] public void HitTargetIsAtLeastThirtySixPixels() { Assert.Contains("MinWidth = 36", ButtonSource); Assert.Contains("MinHeight = 36", ButtonSource); }
    [Fact] public void AllFavoriteEntriesUseUnifiedControl() => Assert.Equal(9, SourceContracts.Count(WindowSource, "controls:FavoriteButton"));
    [Fact] public void FavoriteBindingsAreOneWayFromPersistedModel() => Assert.Equal(9, SourceContracts.Count(WindowSource, "IsFavorite, Mode=OneWay"));
    [Fact] public void CurrentTrackFavoriteButtonsDisableWithoutTrack() => Assert.Equal(3, SourceContracts.Count(WindowSource, "<controls:FavoriteButton IsChecked=\"{Binding CurrentTrack.IsFavorite, Mode=OneWay}\" Command=\"{Binding ToggleFavoriteCommand}\" IsEnabled=\"{Binding CanToggleCurrentFavorite}\""));
    [Fact] public void FavoriteControlCoversQueue() => Assert.Contains("<controls:FavoriteButton Grid.Column=\"3\" Width=\"36\" Height=\"36\" IsChecked=\"{Binding Track.IsFavorite, Mode=OneWay}\"", WindowSource);
    [Fact] public void FavoriteControlCoversPlaylistDetail() => Assert.Contains("ToggleFavoriteCommand", SourceContracts.LineContaining(WindowSource, "RemoveSelectedFromPlaylistCommand"));
    [Fact] public void FavoriteControlCoversTrackPicker() => Assert.Contains("Track.IsFavorite, Mode=OneWay", SourceContracts.LineContaining(WindowSource, "VisibleTrackPickerItems"));
    [Fact] public void OldFavoriteConvertersAreRemoved() { Assert.DoesNotContain("FavoriteVisualConverter", WindowSource); Assert.DoesNotContain("FavoriteGlyphConverter", SourceContracts.Read(@"src\NekoPlayer.App\App.axaml")); }
}

public sealed class FavoritePersistenceV100Tests
{
    [Fact]
    public async Task FavoriteTruePersistsAcrossDbContexts()
    {
        await using var h = await V014Harness.CreateAsync();
        var track = await h.AddTrackAsync();
        await h.Library.SetFavoriteAsync(track.Id, true);
        await using var db = h.CreateDb();
        Assert.True((await db.Tracks.SingleAsync()).IsFavorite);
    }

    [Fact]
    public async Task FavoriteFalsePersistsAcrossDbContexts()
    {
        await using var h = await V014Harness.CreateAsync();
        var track = await h.AddTrackAsync(true);
        await h.Library.SetFavoriteAsync(track.Id, false);
        await using var db = h.CreateDb();
        Assert.False((await db.Tracks.SingleAsync()).IsFavorite);
    }

    [Fact]
    public async Task FavoriteUpdateChangesOnlyTargetTrack()
    {
        await using var h = await V014Harness.CreateAsync();
        var target = await h.AddTrackAsync();
        var other = await h.AddTrackAsync();
        await h.Library.SetFavoriteAsync(target.Id, true);
        await using var db = h.CreateDb();
        Assert.True((await db.Tracks.SingleAsync(x => x.Id == target.Id)).IsFavorite);
        Assert.False((await db.Tracks.SingleAsync(x => x.Id == other.Id)).IsFavorite);
    }

    [Fact] public void ViewModelKeepsRollbackPath() { var text = SourceContracts.Read(@"src\NekoPlayer.App\ViewModels\MainWindowViewModel.cs"); Assert.Contains("ApplyFavoriteState(track.Id, previous)", text); }
    [Fact] public void TrackStateStoreStillPublishesCrossPageEvent() { var text = SourceContracts.Read(@"src\NekoPlayer.Infrastructure\Repositories\MusicLibraryService.cs"); Assert.Contains("PublishFavoriteChanged(trackId, favorite)", text); }
}

public sealed class V100SourceContractTests
{
    [Fact] public void TrackCoverControlIsUsedEverywhereCoversRender() => Assert.Equal(9, SourceContracts.Count(SourceContracts.Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"), "controls:TrackCover"));
    [Fact] public void OldSynchronousCoverConverterIsRemoved() { Assert.DoesNotContain("CoverPathConverter", SourceContracts.Read(@"src\NekoPlayer.App\Converters\UiConverters.cs")); Assert.DoesNotContain("CoverConverter", SourceContracts.Read(@"src\NekoPlayer.App\Views\MainWindow.axaml")); }
    [Fact] public void RealCoverDecodeRunsOffUiThread() => Assert.Contains("Task.Run", SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCoverProvider.cs"));
    [Fact] public void ImageResultReturnsThroughUiDispatcher() => Assert.Contains("Dispatcher.UIThread.InvokeAsync", SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCover.axaml.cs"));
    [Fact] public void TrackCoverDisposesOwnedRealBitmap() => Assert.Contains("_ownedBitmap.Dispose()", SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCover.axaml.cs"));
    [Fact] public void SharedDefaultIsNeverOwnedByOneControl() => Assert.Contains("if (!result.IsDefault) _ownedBitmap = result.Image", SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCover.axaml.cs"));
    [Fact] public void DefaultUsesUniformStretch() => Assert.Contains("result.IsDefault ? Stretch.Uniform", SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCover.axaml.cs"));
    [Fact] public void MissingAndDamagedCoversAreLogged() { var text = SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCoverProvider.cs"); Assert.Contains("歌曲封面缓存不存在", text); Assert.Contains("读取或解码失败", text); }
    [Fact] public void AppIconIsAnAvaloniaResource() => Assert.Contains("<AvaloniaResource Include=\"Assets\\**\"", SourceContracts.Read(@"src\NekoPlayer.App\NekoPlayer.App.csproj"));
    [Fact] public void VersionIsOnePointZero() => Assert.Contains("<Version>1.0.0</Version>", SourceContracts.Read(@"src\NekoPlayer.App\NekoPlayer.App.csproj"));
    [Fact] public void PackageVersionIsOnePointZero() => Assert.Contains("<PackageVersion>1.0.0</PackageVersion>", SourceContracts.Read(@"src\NekoPlayer.App\NekoPlayer.App.csproj"));
    [Fact] public void AssemblyVersionIsOnePointZero() => Assert.Contains("<AssemblyVersion>1.0.0.0</AssemblyVersion>", SourceContracts.Read(@"src\NekoPlayer.App\NekoPlayer.App.csproj"));
    [Fact] public void FileVersionIsOnePointZero() => Assert.Contains("<FileVersion>1.0.0.0</FileVersion>", SourceContracts.Read(@"src\NekoPlayer.App\NekoPlayer.App.csproj"));
    [Fact] public void InformationalVersionIsOnePointZero() => Assert.Contains("<InformationalVersion>1.0.0</InformationalVersion>", SourceContracts.Read(@"src\NekoPlayer.App\NekoPlayer.App.csproj"));
    [Fact] public void ProductVersionDoesNotAppendCommitSha() => Assert.Contains("<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>", SourceContracts.Read(@"src\NekoPlayer.App\NekoPlayer.App.csproj"));
    [Fact] public void LeftFooterHasNoReleaseSubtitle() { var text = SourceContracts.Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"); Assert.DoesNotContain("响应式交互版", text); Assert.DoesNotContain("播放控制与曲库操作修复版", text); }
    [Fact] public void SettingsShowsOnlyDisplayVersion() { var text = SourceContracts.Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"); Assert.DoesNotContain("应用版本：", text); Assert.Contains("StringFormat='v{0}'", text); }
    [Fact] public void CreatorRemainsExact() => Assert.Contains("创作者：梦怀殇", SourceContracts.Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"));
    [Fact] public void ViewModelUsesCentralVersionConstant() => Assert.Contains("AppVersionInfo.Version", SourceContracts.Read(@"src\NekoPlayer.App\ViewModels\MainWindowViewModel.cs"));
    [Fact] public void PublishScriptExpectsOnePointZero() { var text = SourceContracts.Read("publish-win-x64.ps1"); Assert.Contains("$version = '1.0.0'", text); Assert.Contains("'1.0.0.0'", text); }
    [Fact] public void ReadmeDeclaresOnePointZero() => Assert.Contains("v1.0.0", SourceContracts.Read("README.md"));
    [Fact] public void HistoricalAcceptanceDocumentStillExists() => Assert.True(File.Exists(Path.Combine(SourceContracts.Root, "docs", "MANUAL-ACCEPTANCE-v0.1.4.md")));
    [Fact] public void NewAcceptanceDocumentExists() => Assert.True(File.Exists(Path.Combine(SourceContracts.Root, "docs", "MANUAL-ACCEPTANCE-v1.0.0.md")));
    [Fact] public void GuiValidationCanUseExplicitIsolatedDataRoot() { var text = SourceContracts.Read(@"src\NekoPlayer.Infrastructure\Configuration\UserDataPaths.cs"); Assert.Contains("NEKOPLAYER_DATA_ROOT", text); Assert.Contains("Path.GetFullPath(overrideRoot)", text); }
}

internal static class SourceContracts
{
    public static string Root { get; } = FindRoot();
    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, Normalize(relative)));
    public static int Count(string text, string value) => (text.Length - text.Replace(value, string.Empty).Length) / value.Length;
    public static string LineContaining(string text, string value) => text.Split('\n').Single(line => line.Contains(value, StringComparison.Ordinal));
    private static string Normalize(string relative) => relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    private static string FindRoot() { for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "NekoPlayer.sln"))) return d.FullName; throw new DirectoryNotFoundException(); }
}

internal static class AvaloniaTestRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;
            AppBuilder.Configure<TestApplication>().UsePlatformDetect().SetupWithoutStarting();
            _initialized = true;
        }
    }

    private sealed class TestApplication : Application { }
}
