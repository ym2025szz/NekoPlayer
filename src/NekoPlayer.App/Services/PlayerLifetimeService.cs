using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Serilog;

namespace NekoPlayer.App.Services;

/// <summary>Coordinates tray visibility and orderly application shutdown on the UI thread.</summary>
public sealed class PlayerLifetimeService : IDisposable
{
    private Application? _application;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Window? _window;
    private TrayIcon? _tray;
    private Func<bool> _closeToTray = () => true;
    private Func<Task>? _beforeShutdown;
    private readonly TaskCompletionSource _shutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _shutdownRequested;
    private bool _windowClosed;
    private bool _disposed;

    public bool IsTrayAvailable => _tray is { NativeMenuExporter: not null, IsVisible: true };
    public bool IsShutdownRequested => _shutdownRequested;

    public void Configure(Application application, IClassicDesktopStyleApplicationLifetime desktop, Window window,
        Func<bool> closeToTray, ICommand togglePlay, ICommand previous, ICommand next,
        Action toggleDesktopLyrics, Action unlockDesktopLyrics, Func<bool> desktopLyricsEnabled,
        Func<Task>? beforeShutdown = null)
    {
        if (_window is not null) throw new InvalidOperationException("应用生命周期已经配置。");
        ObjectDisposedException.ThrowIf(_disposed, this);
        _application = application;
        _desktop = desktop;
        _window = window;
        _closeToTray = closeToTray;
        _beforeShutdown = beforeShutdown;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.ShutdownRequested += OnShutdownRequested;
        window.Closed += OnWindowClosed;
        TryCreateTray(togglePlay, previous, next, toggleDesktopLyrics, unlockDesktopLyrics, desktopLyricsEnabled);
        Program.Instance?.StartServer(HandleInstanceCommandAsync);
    }

    /// <summary>Call first in MainWindow.OnClosing; false continues the existing save/cleanup path.</summary>
    public bool TryHideOnClose(WindowClosingEventArgs e)
    {
        if (_shutdownRequested || _disposed || _window is null) return false;
        if (!IsTrayAvailable || !_closeToTray()
            || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown or WindowCloseReason.OwnerWindowClosing)
        {
            _shutdownRequested = true;
            return false;
        }
        e.Cancel = true;
        _window.Hide();
        return true;
    }

    public void ShowMainWindow()
    {
        if (_shutdownRequested || _disposed || _window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public Task RequestShutdownAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => _ = RequestShutdownAsync());
            return _shutdownCompletion.Task;
        }
        if (_shutdownRequested || _disposed) return _shutdownCompletion.Task;
        _shutdownRequested = true;
        if (_window is null) _shutdownCompletion.TrySetResult();
        else _window.Close(); // Existing OnClosing awaits SaveStateAsync and closes again.
        return _shutdownCompletion.Task;
    }

    private Task<bool> HandleInstanceCommandAsync(InstanceCommand command)
    {
        // Scheduling is acknowledged promptly; the updater waits for the lifetime mutex
        // rather than this connection to confirm that all cleanup has finished.
        if (_disposed) return Task.FromResult(false);
        if (command == InstanceCommand.Activate && _shutdownRequested) return Task.FromResult(false);
        Dispatcher.UIThread.Post(() =>
        {
            if (command == InstanceCommand.Activate) ShowMainWindow();
            else _ = RequestShutdownAsync();
        });
        return Task.FromResult(true);
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_windowClosed) return;
        e.Cancel = true;
        _ = RequestShutdownAsync();
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_windowClosed) return;
        _windowClosed = true;
        _shutdownRequested = true;
        RemoveTray();
        try
        {
            if (_beforeShutdown is not null) await _beforeShutdown();
        }
        catch (Exception ex) { Log.Warning(ex, "退出时附加服务回收失败"); }
        finally
        {
            _desktop?.Shutdown();
            _shutdownCompletion.TrySetResult();
        }
    }

    private void TryCreateTray(ICommand togglePlay, ICommand previous, ICommand next,
        Action toggleDesktopLyrics, Action unlockDesktopLyrics, Func<bool> desktopLyricsEnabled)
    {
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://NekoPlayer/Assets/NekoPlayer.ico"));
            var menu = new NativeMenu();
            menu.Items.Add(Item("显示主窗口", ShowMainWindow));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(CommandItem("播放 / 暂停", togglePlay));
            menu.Items.Add(CommandItem("上一首", previous));
            menu.Items.Add(CommandItem("下一首", next));
            menu.Items.Add(new NativeMenuItemSeparator());
            var lyrics = Item("桌面歌词", toggleDesktopLyrics);
            lyrics.ToggleType = NativeMenuItemToggleType.CheckBox;
            menu.Items.Add(lyrics);
            menu.Items.Add(Item("解除桌面歌词锁定", unlockDesktopLyrics));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(Item("彻底退出", () => _ = RequestShutdownAsync()));
            menu.NeedsUpdate += (_, _) => lyrics.IsChecked = desktopLyricsEnabled();
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(iconStream), ToolTipText = "猫娘播放器", Menu = menu, IsVisible = true
            };
            // A managed TrayIcon can exist without native tray/menu support.
            if (tray.NativeMenuExporter is null)
            {
                tray.Dispose();
                Log.Information("系统托盘不可用，关闭主窗口将退出应用");
                return;
            }
            tray.Clicked += OnTrayClicked;
            _tray = tray;
            TrayIcon.SetIcons(_application!, new TrayIcons { tray });
        }
        catch (Exception ex) { Log.Warning(ex, "创建系统托盘失败，关闭主窗口将退出应用"); }
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowMainWindow();

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private static NativeMenuItem CommandItem(string header, ICommand command) => new(header) { Command = command };

    private void RemoveTray()
    {
        if (_tray is null) return;
        _tray.Clicked -= OnTrayClicked;
        _tray.IsVisible = false;
        if (_application is not null) TrayIcon.SetIcons(_application, null);
        else _tray.Dispose();
        _tray = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window is not null) _window.Closed -= OnWindowClosed;
        if (_desktop is not null) _desktop.ShutdownRequested -= OnShutdownRequested;
        RemoveTray();
        _shutdownCompletion.TrySetResult();
    }
}
