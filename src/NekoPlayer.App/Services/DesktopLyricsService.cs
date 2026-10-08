using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using NekoPlayer.App.Views;
using NekoPlayer.Core.Models;

namespace NekoPlayer.App.Services;

public interface IDesktopLyricsService
{
    bool IsEnabled { get; }
    bool IsLocked { get; }
    DesktopLyricsSettings Settings { get; }
    event EventHandler? SettingsChanged;
    void Toggle();
    void SetEnabled(bool enabled);
    void Unlock();
    void Apply(DesktopLyricsSettings? settings);
    DesktopLyricsSettings Export();
}

/// <summary>Projects shared lyrics into one unowned window; persistence belongs to the main settings owner.</summary>
public sealed class DesktopLyricsService : IDesktopLyricsService, IDisposable
{
    private readonly LyricsPresentationService _lyrics;
    private DesktopLyricsSettings _settings = new();
    private DesktopLyricsWindow? _window;
    private bool _disposed;
    private bool _applying;

    public DesktopLyricsService(LyricsPresentationService lyrics)
    {
        _lyrics = lyrics;
        _lyrics.PropertyChanged += OnLyricsChanged;
    }

    public bool IsEnabled => _settings.IsEnabled;
    public bool IsLocked => _settings.IsLocked;
    public DesktopLyricsSettings Settings => Export();
    public event EventHandler? SettingsChanged;

    public void Apply(DesktopLyricsSettings? settings) => OnUi(() =>
    {
        _settings = DesktopLyricsPlacement.Normalize(settings);
        UpdateWindow();
    });

    public DesktopLyricsSettings Export() => _settings.Copy();

    public void Toggle() => SetEnabled(!IsEnabled);

    public void SetEnabled(bool enabled) => OnUi(() =>
    {
        if (_settings.IsEnabled == enabled) return;
        _settings.IsEnabled = enabled;
        UpdateWindow();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>The tray escape hatch also reopens a hidden subtitle window.</summary>
    public void Unlock() => OnUi(() =>
    {
        _settings.IsEnabled = true;
        _settings.IsLocked = false;
        UpdateWindow();
        _window?.RevealTools();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    });

    private void UpdateWindow()
    {
        if (!_settings.IsEnabled)
        {
            _window?.Hide();
            return;
        }
        if (_window is null)
        {
            _window = new DesktopLyricsWindow();
            _window.SettingsEdited += OnSettingsEdited;
            _window.GeometryEdited += OnGeometryEdited;
            _window.HideRequested += OnHideRequested;
        }
        _applying = true;
        try
        {
            _window.ApplySettings(_settings);
            _window.EnsureVisiblePlacement(_settings);
            UpdateLyrics();
            // Deliberately no owner: minimizing the player must not minimize the subtitles.
            if (!_window.IsVisible) _window.Show();
            _window.ApplyNativeInteraction();
            CaptureGeometry();
        }
        finally { _applying = false; }
    }

    private void OnSettingsEdited(object? sender, DesktopLyricsSettings settings)
    {
        if (_disposed || _applying) return;
        _settings = DesktopLyricsPlacement.Normalize(settings);
        UpdateWindow();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnGeometryEdited(object? sender, EventArgs e)
    {
        if (_disposed || _applying || _window is null) return;
        CaptureGeometry();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CaptureGeometry()
    {
        if (_window is null) return;
        _settings = _window.ExportSettings(_settings);
    }

    private void OnHideRequested(object? sender, EventArgs e) => SetEnabled(false);

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricsPresentationService.CurrentText) or nameof(LyricsPresentationService.NextText)
            or nameof(LyricsPresentationService.StatusText) or null or "")
            OnUi(UpdateLyrics);
    }

    private void UpdateLyrics() => _window?.UpdateLyrics(
        string.IsNullOrWhiteSpace(_lyrics.CurrentText) ? _lyrics.StatusText : _lyrics.CurrentText,
        _lyrics.NextText);

    private void OnUi(Action action)
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) action(); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lyrics.PropertyChanged -= OnLyricsChanged;
        var window = _window;
        _window = null;
        if (window is null) return;
        window.SettingsEdited -= OnSettingsEdited;
        window.GeometryEdited -= OnGeometryEdited;
        window.HideRequested -= OnHideRequested;
        if (Dispatcher.UIThread.CheckAccess()) window.CloseForShutdown();
        else Dispatcher.UIThread.Post(window.CloseForShutdown);
    }
}

public sealed record DesktopLyricsDisplay(string Name, PixelRect WorkingArea, double Scaling, bool IsPrimary = false);
public sealed record DesktopLyricsBounds(PixelPoint Position, double Width, double Height, DesktopLyricsDisplay Display);

/// <summary>Pure placement rules shared by startup, display changes, and unit tests.</summary>
public static class DesktopLyricsPlacement
{
    public static double HeightForFontSize(double fontSize) => fontSize * 2.5 + 74;

    public static DesktopLyricsSettings Normalize(DesktopLyricsSettings? source)
    {
        var settings = source?.Copy() ?? new DesktopLyricsSettings();
        settings.FontSize = ClampFinite(settings.FontSize, 22, 48, 28);
        settings.Opacity = ClampFinite(settings.Opacity, .6, 1, 1);
        settings.Width = ClampFinite(settings.Width, 120, 3000, 720);
        settings.Height = ClampFinite(settings.Height, HeightForFontSize(settings.FontSize), 600, HeightForFontSize(settings.FontSize));
        settings.ScreenScaling = ClampFinite(settings.ScreenScaling, .25, 8, 1);
        return settings;
    }

    public static DesktopLyricsBounds Resolve(DesktopLyricsSettings? source, IReadOnlyList<DesktopLyricsDisplay> displays)
    {
        if (displays.Count == 0) throw new ArgumentException("至少需要一个可用工作区。", nameof(displays));
        var settings = Normalize(source);
        var primary = displays.FirstOrDefault(x => x.IsPrimary) ?? displays[0];
        var hasPosition = settings.X.HasValue && settings.Y.HasValue;
        var named = string.IsNullOrEmpty(settings.ScreenName) ? null : displays.FirstOrDefault(x => x.Name == settings.ScreenName);
        DesktopLyricsDisplay display;
        double? x = null, y = null;
        if (hasPosition && named is not null)
        {
            display = named;
            var scale = ValidScaling(display.Scaling) / settings.ScreenScaling;
            x = display.WorkingArea.X + ((double)settings.X!.Value - settings.ScreenWorkAreaX) * scale;
            y = display.WorkingArea.Y + ((double)settings.Y!.Value - settings.ScreenWorkAreaY) * scale;
        }
        else if (hasPosition && string.IsNullOrEmpty(settings.ScreenName))
        {
            display = displays.OrderByDescending(d => IntersectionArea(settings, d)).First();
            if (IntersectionArea(settings, display) > 0) { x = settings.X; y = settings.Y; }
            else display = primary;
        }
        else display = primary;

        var area = display.WorkingArea;
        var scaling = ValidScaling(display.Scaling);
        var width = Math.Min(settings.Width, Math.Max(1, area.Width / scaling));
        var height = Math.Min(settings.Height, Math.Max(1, area.Height / scaling));
        var pixelWidth = Math.Min(area.Width, Math.Max(1, (int)Math.Ceiling(width * scaling)));
        var pixelHeight = Math.Min(area.Height, Math.Max(1, (int)Math.Ceiling(height * scaling)));
        x ??= area.X + (area.Width - pixelWidth) / 2d;
        y ??= area.Y + area.Height - pixelHeight - 24 * scaling;
        var left = (int)Math.Clamp(x.Value, area.X, (double)area.X + area.Width - pixelWidth);
        var top = (int)Math.Clamp(y.Value, area.Y, (double)area.Y + area.Height - pixelHeight);
        return new DesktopLyricsBounds(new PixelPoint(left, top), width, height, display);
    }

    private static double IntersectionArea(DesktopLyricsSettings settings, DesktopLyricsDisplay display)
    {
        var area = display.WorkingArea;
        var right = settings.X!.Value + settings.Width * ValidScaling(display.Scaling);
        var bottom = settings.Y!.Value + settings.Height * ValidScaling(display.Scaling);
        return Math.Max(0, Math.Min(right, area.Right) - Math.Max(settings.X.Value, area.X)) *
               Math.Max(0, Math.Min(bottom, area.Bottom) - Math.Max(settings.Y.Value, area.Y));
    }

    private static double ValidScaling(double scaling) => double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
    private static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>Preserve Avalonia's layered/transparency flags while adding only subtitle interaction flags.</summary>
public static class DesktopLyricsNativeStyles
{
    public const uint NoActivate = 0x08000000;
    public const uint ToolWindow = 0x00000080;
    public const uint Transparent = 0x00000020;

    public static uint MergeExtendedStyle(uint current, bool locked, bool originallyTransparent = false)
    {
        var result = current | NoActivate | ToolWindow;
        return locked || originallyTransparent ? result | Transparent : result & ~Transparent;
    }
}
