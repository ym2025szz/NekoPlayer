using System.Text.Json;
using Avalonia;
using NekoPlayer.App.Services;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Tests;

public sealed class DesktopLyricsPlacementTests
{
    [Fact]
    public void ExistingSettingsFileKeepsNewFeaturesAtTheirSafeDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"Volume\":0.42}")!;
        Assert.False(settings.DesktopLyrics.IsEnabled);
        Assert.False(settings.DesktopLyrics.IsLocked);
        Assert.True(settings.CloseToTray);
        Assert.True(settings.SystemMediaControlsEnabled);
        Assert.Equal(28, settings.DesktopLyrics.FontSize);
    }

    [Fact]
    public void FirstEnableCentersOnPrimaryNearBottomOfWorkArea()
    {
        var displays = new[]
        {
            new DesktopLyricsDisplay("left", new PixelRect(-1920, 0, 1920, 1040), 1),
            new DesktopLyricsDisplay("primary", new PixelRect(0, 0, 1920, 1040), 1, true)
        };
        var bounds = DesktopLyricsPlacement.Resolve(new DesktopLyricsSettings(), displays);
        Assert.Equal("primary", bounds.Display.Name);
        Assert.Equal(new PixelPoint(600, 872), bounds.Position);
        Assert.Equal(720, bounds.Width);
        Assert.Equal(144, bounds.Height);
    }

    [Fact]
    public void NarrowHighDpiScreenShrinksWidthAndKeepsTheEntireSubtitleVisible()
    {
        var display = new DesktopLyricsDisplay("primary", new PixelRect(0, 20, 800, 580), 2, true);
        var bounds = DesktopLyricsPlacement.Resolve(new DesktopLyricsSettings(), [display]);
        Assert.Equal(400, bounds.Width);
        Assert.Equal(0, bounds.Position.X);
        AssertInside(bounds);
    }

    [Fact]
    public void NegativeMonitorCoordinatesAreNotMistakenForInvalidPositions()
    {
        var display = new DesktopLyricsDisplay("left", new PixelRect(-1920, -200, 1920, 1040), 1);
        var settings = new DesktopLyricsSettings
        {
            X = -1800, Y = 500, ScreenName = "left", ScreenWorkAreaX = -1920, ScreenWorkAreaY = -200
        };
        var bounds = DesktopLyricsPlacement.Resolve(settings, [display]);
        Assert.Equal(new PixelPoint(-1800, 500), bounds.Position);
        AssertInside(bounds);
    }

    [Fact]
    public void RemovedMonitorFallsBackToVisiblePrimaryDefault()
    {
        var display = new DesktopLyricsDisplay("primary", new PixelRect(0, 0, 1920, 1040), 1, true);
        var settings = new DesktopLyricsSettings { ScreenName = "removed", X = -3000, Y = 2000 };
        var bounds = DesktopLyricsPlacement.Resolve(settings, [display]);
        Assert.Equal(new PixelPoint(600, 872), bounds.Position);
        AssertInside(bounds);
    }

    [Theory]
    [InlineData(int.MaxValue, int.MinValue)]
    [InlineData(-800, 2000)]
    [InlineData(1800, 1000)]
    public void KnownMonitorClampsBothEdgesAfterWorkAreaShrinks(int x, int y)
    {
        var display = new DesktopLyricsDisplay("primary", new PixelRect(0, 40, 1280, 640), 1, true);
        var settings = new DesktopLyricsSettings { ScreenName = "primary", X = x, Y = y };
        AssertInside(DesktopLyricsPlacement.Resolve(settings, [display]));
    }

    [Fact]
    public void MonitorRepositionAndDpiChangePreserveItsRelativeLogicalPosition()
    {
        var display = new DesktopLyricsDisplay("secondary", new PixelRect(0, -100, 2560, 1440), 2);
        var settings = new DesktopLyricsSettings
        {
            ScreenName = "secondary", X = -1800, Y = 250,
            ScreenWorkAreaX = -1920, ScreenWorkAreaY = 50, ScreenScaling = 1
        };
        var bounds = DesktopLyricsPlacement.Resolve(settings, [display]);
        Assert.Equal(new PixelPoint(240, 300), bounds.Position);
        AssertInside(bounds);
    }

    [Fact]
    public void CorruptedSavedGeometryIsNormalizedWithoutMutatingTheOwnersSettings()
    {
        var settings = new DesktopLyricsSettings
        {
            FontSize = double.NaN, Opacity = .1, Width = double.PositiveInfinity,
            Height = double.NaN, ScreenScaling = 0, X = int.MaxValue, Y = int.MinValue
        };
        var normalized = DesktopLyricsPlacement.Normalize(settings);
        Assert.Equal(28, normalized.FontSize);
        Assert.Equal(.6, normalized.Opacity);
        Assert.Equal(720, normalized.Width);
        Assert.Equal(144, normalized.Height);
        Assert.True(double.IsNaN(settings.FontSize));
        var bounds = DesktopLyricsPlacement.Resolve(settings,
            [new DesktopLyricsDisplay("primary", new PixelRect(0, 0, 1280, 680), 1, true)]);
        AssertInside(bounds);
    }

    [Theory]
    [InlineData(22, 16)]
    [InlineData(28, 22)]
    [InlineData(48, 42)]
    public void TwoLinesHaveRoomAtAllSupportedFontSizes(double current, double next)
    {
        var normalized = DesktopLyricsPlacement.Normalize(new DesktopLyricsSettings { FontSize = current });
        Assert.True(normalized.Height >= current + next + 46);
    }

    [Fact]
    public void SavedPositionWithoutDisplayMetadataChoosesOverlappingMonitor()
    {
        var bounds = DesktopLyricsPlacement.Resolve(new DesktopLyricsSettings { X = -1800, Y = 200 },
        [
            new DesktopLyricsDisplay("primary", new PixelRect(0, 0, 1920, 1040), 1, true),
            new DesktopLyricsDisplay("left", new PixelRect(-1920, 0, 1920, 1040), 1)
        ]);
        Assert.Equal("left", bounds.Display.Name);
        Assert.Equal(new PixelPoint(-1800, 200), bounds.Position);
    }

    [Fact]
    public void LockAndUnlockPreserveLayeredAndUnrelatedWindowStyles()
    {
        const uint original = 0x80080008; // Includes WS_EX_LAYERED plus unrelated high/low flags.
        var locked = DesktopLyricsNativeStyles.MergeExtendedStyle(original, true);
        Assert.Equal(original, locked & original);
        Assert.NotEqual(0u, locked & DesktopLyricsNativeStyles.Transparent);
        var unlocked = DesktopLyricsNativeStyles.MergeExtendedStyle(locked, false);
        Assert.Equal(original | DesktopLyricsNativeStyles.NoActivate | DesktopLyricsNativeStyles.ToolWindow, unlocked);
        Assert.Equal(0u, unlocked & DesktopLyricsNativeStyles.Transparent);
    }

    [Fact]
    public void UnlockRetainsTransparencyWhenThePlatformOriginallyRequiredIt()
    {
        var original = 0x00080000u | DesktopLyricsNativeStyles.Transparent;
        var unlocked = DesktopLyricsNativeStyles.MergeExtendedStyle(original, false, originallyTransparent: true);
        Assert.Equal(original, unlocked & original);
    }

    private static void AssertInside(DesktopLyricsBounds bounds)
    {
        var area = bounds.Display.WorkingArea;
        Assert.True(bounds.Position.X >= area.X);
        Assert.True(bounds.Position.Y >= area.Y);
        Assert.True(bounds.Position.X + Math.Ceiling(bounds.Width * bounds.Display.Scaling) <= area.Right);
        Assert.True(bounds.Position.Y + Math.Ceiling(bounds.Height * bounds.Display.Scaling) <= area.Bottom);
    }
}
