using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NekoPlayer.App.Services;
using NekoPlayer.Core.Models;

namespace NekoPlayer.App.Views;

public partial class DesktopLyricsWindow : Window
{
    private DesktopLyricsSettings _settings = new();
    private bool _applying;
    private bool _closingForShutdown;
    private bool _originallyTransparent;
    private bool _recoveryQueued;
    private bool _resizing;
    private PixelPoint _resizeStart;
    private double _resizeWidth;

    public DesktopLyricsWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows())
        {
            var handle = TryGetPlatformHandle()?.Handle ?? nint.Zero;
            if (handle != nint.Zero)
                _originallyTransparent = (ReadExtendedStyle(handle) & DesktopLyricsNativeStyles.Transparent) != 0;
            Win32Properties.AddWindowStylesCallback(this, MergeWindowStyles);
            Win32Properties.AddWndProcHookCallback(this, OnWndProc);
        }
        Opened += (_, _) => ApplyNativeInteraction();
        Closing += (_, e) =>
        {
            if (_closingForShutdown) return;
            e.Cancel = true;
            HideRequested?.Invoke(this, EventArgs.Empty);
        };
        PositionChanged += (_, _) => ReportGeometry();
        SizeChanged += (_, _) => ReportGeometry();
        ScalingChanged += (_, _) => QueuePlacementRecovery();
        Screens.Changed += OnDisplaysChanged;
        Closed += (_, _) =>
        {
            Screens.Changed -= OnDisplaysChanged;
            if (OperatingSystem.IsWindows())
            {
                Win32Properties.RemoveWindowStylesCallback(this, MergeWindowStyles);
                Win32Properties.RemoveWndProcHookCallback(this, OnWndProc);
            }
        };
    }

    public event EventHandler<DesktopLyricsSettings>? SettingsEdited;
    public event EventHandler? GeometryEdited;
    public event EventHandler? HideRequested;

    public void UpdateLyrics(string? current, string? next)
    {
        CurrentLyric.Text = current ?? "";
        NextLyric.Text = next ?? "";
    }

    public void ApplySettings(DesktopLyricsSettings settings)
    {
        _applying = true;
        try
        {
            _settings = DesktopLyricsPlacement.Normalize(settings);
            CurrentLyric.FontSize = _settings.FontSize;
            NextLyric.FontSize = _settings.FontSize - 6;
            CurrentLyric.Opacity = NextLyric.Opacity = _settings.Opacity;
            FontSizeLabel.Text = $"{_settings.FontSize:0}";
            OpacityLabel.Text = $"{_settings.Opacity:P0}";
            if (_settings.IsLocked) SetToolsVisible(false);
            ApplyNativeInteraction();
        }
        finally { _applying = false; }
    }

    public void EnsureVisiblePlacement(DesktopLyricsSettings settings)
    {
        var displays = Screens.All.Where(x => x.WorkingArea.Width > 0 && x.WorkingArea.Height > 0)
            .Select(x => new DesktopLyricsDisplay(x.DisplayName ?? x.Bounds.ToString(), x.WorkingArea, x.Scaling, x.IsPrimary))
            .ToArray();
        if (displays.Length == 0) return;
        var placement = DesktopLyricsPlacement.Resolve(settings, displays);
        _applying = true;
        try
        {
            // Even an unusually small work area must be able to contain the complete overlay.
            MinWidth = MinHeight = 0;
            Width = placement.Width;
            Height = placement.Height;
            Position = placement.Position;
            _settings = ExportSettings(_settings);
        }
        finally { _applying = false; }
    }

    public DesktopLyricsSettings ExportSettings(DesktopLyricsSettings current)
    {
        var result = current.Copy();
        result.X = Position.X;
        result.Y = Position.Y;
        result.Width = double.IsFinite(Width) ? Width : 720;
        result.Height = double.IsFinite(Height) ? Height : DesktopLyricsPlacement.HeightForFontSize(result.FontSize);
        var screen = Screens.ScreenFromWindow(this);
        if (screen is not null)
        {
            result.ScreenName = screen.DisplayName ?? screen.Bounds.ToString();
            result.ScreenScaling = screen.Scaling;
            result.ScreenWorkAreaX = screen.WorkingArea.X;
            result.ScreenWorkAreaY = screen.WorkingArea.Y;
        }
        _settings = result.Copy();
        return result;
    }

    public void RevealTools() => SetToolsVisible(!_settings.IsLocked);

    public void CloseForShutdown()
    {
        _closingForShutdown = true;
        Close();
    }

    public void ApplyNativeInteraction()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (handle == nint.Zero) return;
        var current = ReadExtendedStyle(handle);
        var merged = DesktopLyricsNativeStyles.MergeExtendedStyle(current, _settings.IsLocked, _originallyTransparent);
        if (current != merged) WriteExtendedStyle(handle, merged);
        SetWindowPos(handle, new nint(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020);
    }

    private (uint Style, uint ExtendedStyle) MergeWindowStyles(uint style, uint extendedStyle) =>
        (style, DesktopLyricsNativeStyles.MergeExtendedStyle(extendedStyle, _settings.IsLocked, _originallyTransparent));

    private nint OnWndProc(nint hwnd, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE: subtitle controls never steal keyboard focus.
        {
            handled = true;
            return new nint(3); // MA_NOACTIVATE
        }
        if (_settings.IsLocked && message == 0x0084)
        {
            handled = true;
            // Keep the hit-test fallback alongside WS_EX_TRANSPARENT. The native verifier checks
            // cross-process WindowFromPoint with Avalonia's actual DComp/no-redirection surface.
            return new nint(-1); // HTTRANSPARENT
        }
        if (message is 0x007E or 0x02E0) QueuePlacementRecovery();
        return nint.Zero;
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => QueuePlacementRecovery();

    private void QueuePlacementRecovery()
    {
        if (_closingForShutdown || _recoveryQueued) return;
        _recoveryQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _recoveryQueued = false;
            if (_closingForShutdown) return;
            EnsureVisiblePlacement(_settings);
            ReportGeometry();
        }, DispatcherPriority.Background);
    }

    private void ReportGeometry()
    {
        if (_applying || _closingForShutdown) return;
        _settings = ExportSettings(_settings);
        GeometryEdited?.Invoke(this, EventArgs.Empty);
    }

    private void SetToolsVisible(bool visible)
    {
        Toolbar.IsVisible = visible;
        ResizeHandle.IsVisible = visible;
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e) => SetToolsVisible(!_settings.IsLocked);
    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_resizing) SetToolsVisible(false);
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_settings.IsLocked || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_settings.IsLocked || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _resizing = true;
        _resizeStart = this.PointToScreen(e.GetPosition(this));
        _resizeWidth = Width;
        e.Pointer.Capture(ResizeHandle);
        e.Handled = true;
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing) return;
        var screen = Screens.ScreenFromWindow(this);
        var scale = screen?.Scaling ?? RenderScaling;
        var point = this.PointToScreen(e.GetPosition(this));
        var maximum = screen is null ? 3000 : Math.Max(1, ((double)screen.WorkingArea.Right - Position.X) / scale);
        Width = Math.Clamp(_resizeWidth + (point.X - _resizeStart.X) / scale, Math.Min(120, maximum), maximum);
        e.Handled = true;
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);
        ReportGeometry();
        e.Handled = true;
    }

    private void OnResizeCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _resizing = false;
        ReportGeometry();
    }

    private void Edit(Action<DesktopLyricsSettings> edit)
    {
        if (_settings.IsLocked) return;
        var settings = ExportSettings(_settings);
        edit(settings);
        SettingsEdited?.Invoke(this, settings);
    }

    private void OnSmallerClick(object? sender, RoutedEventArgs e) => ChangeFont(-2);
    private void OnLargerClick(object? sender, RoutedEventArgs e) => ChangeFont(2);
    private void ChangeFont(double delta) => Edit(settings =>
    {
        settings.FontSize = Math.Clamp(settings.FontSize + delta, 22, 48);
        settings.Height = DesktopLyricsPlacement.HeightForFontSize(settings.FontSize);
    });
    private void OnLessOpaqueClick(object? sender, RoutedEventArgs e) => Edit(s => s.Opacity = Math.Max(.6, s.Opacity - .1));
    private void OnMoreOpaqueClick(object? sender, RoutedEventArgs e) => Edit(s => s.Opacity = Math.Min(1, s.Opacity + .1));
    private void OnLockClick(object? sender, RoutedEventArgs e) => Edit(s => s.IsLocked = true);
    private void OnHideClick(object? sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private static uint ReadExtendedStyle(nint handle) => unchecked((uint)(nint.Size == 8
        ? GetWindowLongPtr(handle, -20).ToInt64() : GetWindowLong(handle, -20)));
    private static void WriteExtendedStyle(nint handle, uint style)
    {
        if (nint.Size == 8) SetWindowLongPtr(handle, -20, new nint(unchecked((long)style)));
        else SetWindowLong(handle, -20, unchecked((int)style));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
