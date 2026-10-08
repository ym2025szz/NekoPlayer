using System.Security.Cryptography;
using System.Xml.Linq;
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
    [Fact]
    public void AutomationNameTracksState()
    {
        // Preserve the action's favorite-state wording while allowing song/source context.
        // This source contract does not require creating a dispatcher-bound control in xUnit workers.
        Assert.Contains("var accessibleText = favorite ? RemoveTooltip : AddTooltip", ButtonSource);
        Assert.Contains("ToolTip.SetTip(this, accessibleText)", ButtonSource);
        Assert.Contains("AutomationProperties.SetName(this,", ButtonSource);
        Assert.Contains("string.IsNullOrWhiteSpace(AutomationContext) ? accessibleText", ButtonSource);
        Assert.Contains("{accessibleText}，{AutomationContext}", ButtonSource);
        Assert.Contains("change.Property == IsCheckedProperty", ButtonSource);
        Assert.Contains("change.Property == AutomationContextProperty", ButtonSource);
    }
    [Fact] public void FormalControlDoesNotUseUnicodeHearts() { Assert.DoesNotContain("♡", ButtonSource); Assert.DoesNotContain("♥", ButtonSource); }
    [Fact] public void HoverKeepsDistinctColors() { Assert.Contains("#C4D7D9", ButtonSource); Assert.Contains("#FF7892", ButtonSource); }
    [Fact] public void CheckedStateHasLightPinkBackground() => Assert.Contains("#22FF5D7D", ButtonSource);
    [Fact] public void DisabledStatePreservesVisibleState() => Assert.Contains("!IsEnabled ? 0.58", ButtonSource);
    [Fact] public void HitTargetIsAtLeastThirtySixPixels() { Assert.Contains("MinWidth = 36", ButtonSource); Assert.Contains("MinHeight = 36", ButtonSource); }
    [Fact]
    public void AllFavoriteEntriesUseUnifiedControl()
    {
        foreach (var surface in XamlSurfaceContracts.TrackSurfaces().Concat(XamlSurfaceContracts.CurrentTrackSurfaces()))
        {
            var favorite = Assert.Single(surface.Descendants(XamlSurfaceContracts.Controls + "FavoriteButton"));
            Assert.Contains("FavoriteCommand", (string?)favorite.Attribute("Command") ?? string.Empty);
            var favoriteAction = Assert.Single(surface.Descendants().Where(element => ((string?)element.Attribute("Command"))?.Contains("FavoriteCommand", StringComparison.Ordinal) is true));
            Assert.Same(favorite, favoriteAction);
            Assert.NotNull(favorite.Attribute("AutomationContext"));
        }
    }

    [Fact]
    public void FavoriteBindingsAreOneWayFromPersistedModel()
    {
        var controls = XamlSurfaceContracts.Window.Descendants(XamlSurfaceContracts.Controls + "FavoriteButton")
            .Concat(XamlSurfaceContracts.Search.Descendants(XamlSurfaceContracts.Controls + "FavoriteButton")).ToArray();
        Assert.NotEmpty(controls);
        Assert.All(controls, control => Assert.EndsWith("IsFavorite, Mode=OneWay}", (string?)control.Attribute("IsChecked") ?? string.Empty));
    }

    [Fact]
    public void CurrentTrackFavoriteButtonsDisableWithoutTrack()
    {
        foreach (var surface in XamlSurfaceContracts.CurrentTrackSurfaces())
        {
            var button = Assert.Single(surface.Descendants(XamlSurfaceContracts.Controls + "FavoriteButton"));
            Assert.Equal("{Binding CurrentTrack.IsFavorite, Mode=OneWay}", (string?)button.Attribute("IsChecked"));
            Assert.Equal("{Binding CanToggleCurrentFavorite}", (string?)button.Attribute("IsEnabled"));
            Assert.Equal("{Binding ToggleFavoriteCommand}", (string?)button.Attribute("Command"));
        }
        Assert.Contains("CanToggleCurrentFavorite => CurrentTrack is not null && !_favoriteOperationInProgress", SourceContracts.Read(@"src\NekoPlayer.App\ViewModels\MainWindowViewModel.cs"));
    }

    [Fact]
    public void NowPlayingHasOneFavoriteActionInEachLayoutVariant()
    {
        var variants = XamlSurfaceContracts.NowPlayingSurfaces().ToArray();
        Assert.Equal(2, variants.Length);
        Assert.All(variants, variant =>
        {
            var button = Assert.Single(variant.Descendants(XamlSurfaceContracts.Controls + "FavoriteButton"));
            Assert.Equal("{Binding CurrentTrack.IsFavorite, Mode=OneWay}", (string?)button.Attribute("IsChecked"));
            Assert.Equal("{Binding CanToggleCurrentFavorite}", (string?)button.Attribute("IsEnabled"));
            Assert.Equal("{Binding ToggleFavoriteCommand}", (string?)button.Attribute("Command"));
            Assert.Equal("{Binding CurrentTrack.AutomationContext}", (string?)button.Attribute("AutomationContext"));
        });
    }

    [Fact]
    public void FavoriteControlCoversQueue()
    {
        var button = Assert.Single(XamlSurfaceContracts.TrackTemplate("QueueTracks").Descendants(XamlSurfaceContracts.Controls + "FavoriteButton"));
        Assert.Equal("{Binding Track.IsFavorite, Mode=OneWay}", (string?)button.Attribute("IsChecked"));
        Assert.Equal("{Binding Track}", (string?)button.Attribute("CommandParameter"));
    }

    [Fact]
    public void FavoriteControlCoversPlaylistDetail()
    {
        var template = XamlSurfaceContracts.TrackTemplate("VisiblePlaylistTracks");
        var button = Assert.Single(template.Descendants(XamlSurfaceContracts.Controls + "FavoriteButton"));
        Assert.Contains("ToggleFavoriteCommand", (string?)button.Attribute("Command") ?? string.Empty);
        Assert.Contains(template.Descendants(), element => ((string?)element.Attribute("Command"))?.Contains("RemoveSelectedFromPlaylistCommand", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void FavoriteControlCoversTrackPicker()
    {
        var button = Assert.Single(XamlSurfaceContracts.TrackTemplate("VisibleTrackPickerItems").Descendants(XamlSurfaceContracts.Controls + "FavoriteButton"));
        Assert.Equal("{Binding Track.IsFavorite, Mode=OneWay}", (string?)button.Attribute("IsChecked"));
        Assert.Equal("{Binding Track}", (string?)button.Attribute("CommandParameter"));
    }
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
    [Fact]
    public void TrackCoverControlIsUsedEverywhereCoversRender()
    {
        foreach (var surface in XamlSurfaceContracts.TrackSurfaces())
        {
            var cover = Assert.Single(surface.Descendants(XamlSurfaceContracts.Controls + "TrackCover"));
            Assert.Contains("CoverCachePath}", (string?)cover.Attribute("CoverPath") ?? string.Empty);
            Assert.Contains("CoverUrl}", (string?)cover.Attribute("CoverUrl") ?? string.Empty);
        }
        foreach (var surface in XamlSurfaceContracts.CurrentTrackSurfaces())
        {
            var cover = Assert.Single(surface.Descendants(XamlSurfaceContracts.Controls + "TrackCover"));
            Assert.Equal("{Binding CurrentCoverPath}", (string?)cover.Attribute("CoverPath"));
            Assert.Equal("{Binding CurrentCoverUrl}", (string?)cover.Attribute("CoverUrl"));
        }
    }
    [Fact] public void OldSynchronousCoverConverterIsRemoved() { Assert.DoesNotContain("CoverPathConverter", SourceContracts.Read(@"src\NekoPlayer.App\Converters\UiConverters.cs")); Assert.DoesNotContain("CoverConverter", SourceContracts.Read(@"src\NekoPlayer.App\Views\MainWindow.axaml")); }
    [Fact] public void RealCoverDecodeRunsOffUiThread() => Assert.Contains("Task.Run", SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCoverProvider.cs"));
    [Fact] public void ImageResultReturnsThroughUiDispatcher() => Assert.Contains("Dispatcher.UIThread.InvokeAsync", SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCover.axaml.cs"));
    [Fact] public void TrackCoverDisposesOwnedRealBitmap() => Assert.Contains("_ownedBitmap.Dispose()", SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCover.axaml.cs"));
    [Fact] public void SharedDefaultIsNeverOwnedByOneControl() => Assert.Contains("if (!result.IsDefault) _ownedBitmap = result.Image", SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCover.axaml.cs"));
    [Fact] public void DefaultUsesUniformStretch() => Assert.Contains("result.IsDefault ? Stretch.Uniform", SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCover.axaml.cs"));
    [Fact] public void MissingAndDamagedCoversAreLogged() { var text = SourceContracts.Read(@"src\NekoPlayer.App\Controls\TrackCoverProvider.cs"); Assert.Contains("歌曲封面缓存不存在", text); Assert.Contains("读取或解码失败", text); }
    [Fact] public void AppIconIsAnAvaloniaResource() => Assert.Contains("<AvaloniaResource Include=\"Assets\\**\"", SourceContracts.Read(@"src\NekoPlayer.App\NekoPlayer.App.csproj"));
    [Fact] public void VersionIsCurrentRelease() => Assert.Equal("1.2.0", NekoPlayer.App.AppVersionInfo.Version);
    [Fact] public void PackageVersionUsesProjectVersion() => Assert.Contains("<PackageVersion>$(Version)</PackageVersion>", SourceContracts.Read(@"src\NekoPlayer.App\NekoPlayer.App.csproj"));
    [Fact] public void AssemblyVersionUsesProjectVersion() => Assert.Equal(NekoPlayer.App.AppVersionInfo.Version + ".0", typeof(NekoPlayer.App.AppVersionInfo).Assembly.GetName().Version?.ToString());
    [Fact] public void FileVersionUsesProjectVersion() => Assert.Equal(NekoPlayer.App.AppVersionInfo.Version + ".0", System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(NekoPlayer.App.AppVersionInfo).Assembly.Location).FileVersion);
    [Fact] public void InformationalVersionUsesProjectVersion() => Assert.Equal(NekoPlayer.App.AppVersionInfo.Version, System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(NekoPlayer.App.AppVersionInfo).Assembly.Location).ProductVersion);
    [Fact] public void LeftFooterHasNoReleaseSubtitle() { var text = SourceContracts.Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"); Assert.DoesNotContain("响应式交互版", text); Assert.DoesNotContain("播放控制与曲库操作修复版", text); }
    [Fact] public void SettingsShowsOnlyDisplayVersion() { var text = SourceContracts.Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"); Assert.DoesNotContain("应用版本：", text); Assert.Contains("StringFormat='v{0}'", text); }
    [Fact] public void CreatorRemainsExact() => Assert.Contains("创作者：梦怀殇", SourceContracts.Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"));
    [Fact] public void ViewModelUsesCentralVersionConstant() => Assert.Contains("AppVersionInfo.Version", SourceContracts.Read(@"src\NekoPlayer.App\ViewModels\MainWindowViewModel.cs"));
    [Fact] public void PublishScriptReadsProjectVersion() { var text = SourceContracts.Read("publish-win-x64.ps1"); Assert.Contains("$projectXml.Project.PropertyGroup.Version", text); Assert.Contains("$fileVersion = \"$version.0\"", text); }
    [Fact] public void ReadmeDeclaresCurrentVersion() => Assert.Contains(NekoPlayer.App.AppVersionInfo.DisplayVersion, SourceContracts.Read("README.md"));
    [Fact] public void HistoricalAcceptanceDocumentStillExists() => Assert.True(File.Exists(Path.Combine(SourceContracts.Root, "docs", "MANUAL-ACCEPTANCE-v0.1.4.md")));
    [Fact] public void NewAcceptanceDocumentExists() => Assert.True(File.Exists(Path.Combine(SourceContracts.Root, "docs", $"MANUAL-ACCEPTANCE-{NekoPlayer.App.AppVersionInfo.DisplayVersion}.md")));
    [Fact] public void GuiValidationCanUseExplicitIsolatedDataRoot() { var text = SourceContracts.Read(@"src\NekoPlayer.Infrastructure\Configuration\UserDataPaths.cs"); Assert.Contains("NEKOPLAYER_DATA_ROOT", text); Assert.Contains("Path.GetFullPath(overrideRoot)", text); }
}

internal static class SourceContracts
{
    public static string Root { get; } = FindRoot();
    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));
    public static int Count(string text, string value) => (text.Length - text.Replace(value, string.Empty).Length) / value.Length;
    public static string LineContaining(string text, string value) => text.Split('\n').Single(line => line.Contains(value, StringComparison.Ordinal));
    private static string FindRoot() { for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "NekoPlayer.sln"))) return d.FullName; throw new DirectoryNotFoundException(); }
}

internal static class XamlSurfaceContracts
{
    public static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    public static readonly XNamespace Controls = "using:NekoPlayer.App.Controls";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    public static XDocument Window => XDocument.Parse(SourceContracts.Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"));
    public static XDocument Search => XDocument.Parse(SourceContracts.Read(@"src\NekoPlayer.App\Views\OnlineSearchView.axaml"));

    public static XElement TrackTemplate(string source, XDocument? document = null)
    {
        document ??= Window;
        var list = document.Descendants(Avalonia + "ListBox").Single(x => (string?)x.Attribute("ItemsSource") == $"{{Binding {source}}}");
        if (list.Attribute("ItemTemplate") is { } resource) return SharedTemplate(document, resource.Value);
        var template = list.Elements(Avalonia + "ListBox.ItemTemplate").Single().Elements(Avalonia + "DataTemplate").Single();
        var projection = template.Descendants(Avalonia + "ContentControl").SingleOrDefault(x => x.Attribute("ContentTemplate") is not null);
        if (projection is null) return template;
        // RecentTrack must project its actual Track into the common Track template.
        Assert.Equal("{Binding Track}", (string?)projection.Attribute("Content"));
        return SharedTemplate(document, projection.Attribute("ContentTemplate")!.Value);
    }

    public static IEnumerable<XElement> TrackSurfaces()
    {
        foreach (var source in new[] { "Tracks", "Favorites", "RecentTracks", "VisiblePlaylistTracks", "QueueTracks", "VisibleTrackPickerItems" })
            yield return TrackTemplate(source);
        yield return TrackTemplate("SelectedGroup.Tracks", Search);
    }

    public static IEnumerable<XElement> CurrentTrackSurfaces()
    {
        var document = Window;
        foreach (var surface in NowPlayingSurfaces(document)) yield return surface;
        foreach (var visibility in new[] { "{Binding IsWideLayout}", "{Binding IsWideLayout, Converter={StaticResource BooleanInverseConverter}}" })
            yield return document.Descendants().Single(x => (string?)x.Attribute("IsVisible") == visibility);
    }

    public static IEnumerable<XElement> NowPlayingSurfaces(XDocument? document = null)
    {
        document ??= Window;
        var page = document.Descendants(Avalonia + "Grid").Single(element => (string?)element.Attribute("IsVisible") == "{Binding IsNowPlayingPage}");
        var variants = page.Elements(Avalonia + "Grid").ToArray();
        Assert.Equal(2, variants.Length);
        Assert.Equal(new[]
        {
            "{Binding IsCompactLayout, Converter={StaticResource BooleanInverseConverter}}",
            "{Binding IsCompactLayout}"
        }, variants.Select(variant => (string?)variant.Attribute("IsVisible")));
        return variants;
    }

    private static XElement SharedTemplate(XDocument document, string resource)
    {
        const string prefix = "{StaticResource ";
        Assert.StartsWith(prefix, resource);
        Assert.EndsWith("}", resource);
        var key = resource[prefix.Length..^1].Trim();
        return document.Descendants(Avalonia + "DataTemplate").Single(x => (string?)x.Attribute(Xaml + "Key") == key);
    }
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
