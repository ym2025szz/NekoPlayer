using Avalonia.Controls;
using Avalonia.Threading;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using Serilog;

namespace NekoPlayer.App.Services;

public enum SystemMediaControlButton { Play, Pause, Next, Previous, Stop }
public enum SystemMediaPlaybackStatus { Closed, Changing, Playing, Paused, Stopped }
public enum WindowsMediaControlsStatus { WaitingForWindow, Available, Disabled, Unavailable, Disposed }

public sealed record SystemMediaControlSnapshot(long SessionId, string Title, string Artist,
    string Album, string? CoverCachePath, SystemMediaPlaybackStatus Status, bool HasTrack);

/// <summary>A small boundary around WinRT, allowing control events to be tested without a real media session.</summary>
public interface IWindowsMediaControlsAdapter : IDisposable
{
    event EventHandler<SystemMediaControlButton>? ButtonPressed;
    void SetEnabled(bool enabled);
    void Update(SystemMediaControlSnapshot snapshot);
}

public interface IWindowsMediaPlaybackController
{
    PlaybackSnapshot Snapshot { get; }
    event EventHandler<PlaybackSnapshot>? SnapshotChanged;
    Task PauseAsync();
    Task ResumeAsync();
    Task NextAsync();
    Task PreviousAsync();
    Task StopAsync();
}

/// <summary>
/// Publishes the coordinator's current session to Windows SMTC. The HWND remains valid while the
/// main window is hidden in the tray; only application shutdown should dispose this service.
/// </summary>
public sealed class WindowsMediaControlsService : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly IWindowsMediaPlaybackController _playback;
    private readonly Func<IntPtr, IWindowsMediaControlsAdapter> _createAdapter;
    private readonly Action<Action> _post;
    private IWindowsMediaControlsAdapter? _adapter;
    private PlaybackSnapshot? _latestSnapshot;
    private bool _enabled = true;
    private bool _disposed;
    private bool _applyPending;
    private bool _initializationFailed;
    private long _lifetime;

    public WindowsMediaControlsService(PlaybackCoordinator coordinator)
        : this(new CoordinatorPlaybackController(coordinator ?? throw new ArgumentNullException(nameof(coordinator)))) { }

    public WindowsMediaControlsService(IWindowsMediaPlaybackController playback,
        Func<IntPtr, IWindowsMediaControlsAdapter>? createAdapter = null, Action<Action>? post = null)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _post = post ?? (callback => Dispatcher.UIThread.Post(callback));
        _createAdapter = createAdapter ?? (hwnd => new WinRtWindowsMediaControlsAdapter(hwnd, _post));
        _playback.SnapshotChanged += OnSnapshotChanged;
    }

    public event EventHandler? StatusChanged;

    public bool IsEnabled
    {
        get { lock (_gate) return _enabled; }
        set
        {
            lock (_gate)
            {
                if (_disposed || _enabled == value) return;
                _enabled = value;
                _lifetime++; // A queued hardware event cannot outlive the setting that authorized it.
            }
            NotifyStatusChanged();
            ScheduleApply();
        }
    }

    public bool IsAvailable { get { lock (_gate) return !_disposed && _adapter is not null; } }

    public WindowsMediaControlsStatus Status
    {
        get
        {
            lock (_gate)
                return _disposed ? WindowsMediaControlsStatus.Disposed
                    : !_enabled ? WindowsMediaControlsStatus.Disabled
                    : _adapter is not null ? WindowsMediaControlsStatus.Available
                    : _initializationFailed ? WindowsMediaControlsStatus.Unavailable
                    : WindowsMediaControlsStatus.WaitingForWindow;
        }
    }

    public string StatusText => Status switch
    {
        WindowsMediaControlsStatus.Available => "已启用，可使用键盘媒体键和 Windows 媒体控件。",
        WindowsMediaControlsStatus.Disabled => "已关闭系统媒体控制。",
        WindowsMediaControlsStatus.Unavailable => "当前系统媒体控制不可用，播放器仍可正常播放。",
        WindowsMediaControlsStatus.Disposed => "系统媒体控制已释放。",
        _ => "等待主窗口就绪。"
    };

    /// <summary>Call after the Avalonia main window has opened and owns its native HWND.</summary>
    public bool Initialize(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.HandleDescriptor != "HWND" || handle.Handle == IntPtr.Zero)
        {
            FailIntegration(new InvalidOperationException("主窗口尚未提供 Windows HWND。"));
            return false;
        }
        return Initialize(handle.Handle);
    }

    public bool Initialize(IntPtr hwnd)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_adapter is not null) return true;
        }
        IWindowsMediaControlsAdapter? created = null;
        try
        {
            if (hwnd == IntPtr.Zero) throw new ArgumentException("A valid main-window HWND is required.", nameof(hwnd));
            created = _createAdapter(hwnd);
            var snapshot = _playback.Snapshot;
            lock (_gate)
            {
                if (_disposed || _adapter is not null)
                {
                    created.Dispose();
                    return !_disposed;
                }
                _adapter = created;
                _initializationFailed = false;
                if (_latestSnapshot is null || snapshot.SessionId >= _latestSnapshot.SessionId)
                    _latestSnapshot = snapshot;
                created.ButtonPressed += OnButtonPressed;
            }
            NotifyStatusChanged();
            ScheduleApply();
            return IsAvailable;
        }
        catch (Exception ex)
        {
            // Activation is optional. It must never turn into a playback/load failure.
            if (created is not null && !ReferenceEquals(created, _adapter)) TryDispose(created);
            FailIntegration(ex);
            return false;
        }
    }

    public static SystemMediaPlaybackStatus MapPlaybackStatus(PlaybackSnapshot snapshot) =>
        snapshot.PendingTrack is not null ? SystemMediaPlaybackStatus.Changing : snapshot.State switch
        {
            PlaybackState.Playing => SystemMediaPlaybackStatus.Playing,
            PlaybackState.Paused => SystemMediaPlaybackStatus.Paused,
            PlaybackState.Loading or PlaybackState.Buffering or PlaybackState.Seeking => SystemMediaPlaybackStatus.Changing,
            PlaybackState.Idle => SystemMediaPlaybackStatus.Closed,
            _ => SystemMediaPlaybackStatus.Stopped
        };

    private void OnSnapshotChanged(object? sender, PlaybackSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || (_latestSnapshot is { } current && snapshot.SessionId < current.SessionId)) return;
            _latestSnapshot = snapshot;
        }
        ScheduleApply();
    }

    private void ScheduleApply()
    {
        lock (_gate)
        {
            if (_disposed || _adapter is null || _applyPending) return;
            _applyPending = true;
        }
        TryPost(ApplyLatest);
    }

    private void ApplyLatest()
    {
        IWindowsMediaControlsAdapter? adapter;
        PlaybackSnapshot? snapshot;
        bool enabled;
        lock (_gate)
        {
            _applyPending = false;
            if (_disposed) return;
            adapter = _adapter;
            snapshot = _latestSnapshot;
            enabled = _enabled;
        }
        if (adapter is null) return;
        try
        {
            adapter.SetEnabled(enabled);
            if (enabled && snapshot is not null)
            {
                var track = snapshot.Track;
                adapter.Update(new(snapshot.SessionId, track?.Title ?? string.Empty, track?.Artist ?? string.Empty,
                    track?.Album ?? string.Empty, CachedCoverPath(track?.CoverCachePath), MapPlaybackStatus(snapshot),
                    track is not null || snapshot.PendingTrack is not null));
            }
        }
        catch (Exception ex) { FailIntegration(ex, adapter); }
    }

    private static string? CachedCoverPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return File.Exists(path) ? Path.GetFullPath(path) : null; }
        catch { return null; }
    }

    private void OnButtonPressed(object? sender, SystemMediaControlButton button)
    {
        long lifetime;
        long session;
        lock (_gate)
        {
            if (_disposed || !_enabled || !ReferenceEquals(sender, _adapter) || _latestSnapshot is null) return;
            lifetime = _lifetime;
            session = _latestSnapshot.SessionId;
        }
        TryPost(() => _ = ExecuteButtonAsync(button, lifetime, session));
    }

    private async Task ExecuteButtonAsync(SystemMediaControlButton button, long lifetime, long session)
    {
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
                if (_disposed || !_enabled || _adapter is null || lifetime != _lifetime || _latestSnapshot?.SessionId != session)
                    return;
            switch (button)
            {
                case SystemMediaControlButton.Play: await _playback.ResumeAsync().ConfigureAwait(false); break;
                case SystemMediaControlButton.Pause: await _playback.PauseAsync().ConfigureAwait(false); break;
                case SystemMediaControlButton.Next: await _playback.NextAsync().ConfigureAwait(false); break;
                case SystemMediaControlButton.Previous: await _playback.PreviousAsync().ConfigureAwait(false); break;
                case SystemMediaControlButton.Stop: await _playback.StopAsync().ConfigureAwait(false); break;
            }
        }
        catch (ObjectDisposedException) { /* Shutdown may finish while an already accepted command is awaiting audio. */ }
        catch (Exception ex) { Log.Warning(ex, "Windows 媒体按键操作失败"); }
        finally { _commandGate.Release(); }
    }

    private void TryPost(Action action)
    {
        try { _post(action); }
        catch (Exception ex) { FailIntegration(ex); }
    }

    private void FailIntegration(Exception error, IWindowsMediaControlsAdapter? expected = null)
    {
        IWindowsMediaControlsAdapter? adapter;
        lock (_gate)
        {
            if (_disposed || (expected is not null && !ReferenceEquals(expected, _adapter))) return;
            adapter = _adapter;
            _adapter = null;
            _applyPending = false;
            _initializationFailed = true;
            _lifetime++;
        }
        if (adapter is not null)
        {
            adapter.ButtonPressed -= OnButtonPressed;
            TryDispose(adapter);
        }
        Log.Warning(error, "Windows 系统媒体控制不可用，继续使用播放器内控制");
        NotifyStatusChanged();
    }

    private void NotifyStatusChanged()
    {
        try { StatusChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Warning(ex, "系统媒体控制状态通知失败"); }
    }

    private static void TryDispose(IWindowsMediaControlsAdapter adapter)
    {
        try { adapter.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "系统媒体控制清理失败"); }
    }

    public void Dispose()
    {
        IWindowsMediaControlsAdapter? adapter;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime++;
            adapter = _adapter;
            _adapter = null;
            _latestSnapshot = null;
        }
        _playback.SnapshotChanged -= OnSnapshotChanged;
        if (adapter is not null)
        {
            adapter.ButtonPressed -= OnButtonPressed;
            TryDispose(adapter);
        }
        NotifyStatusChanged();
    }

    private sealed class CoordinatorPlaybackController(PlaybackCoordinator coordinator) : IWindowsMediaPlaybackController
    {
        public PlaybackSnapshot Snapshot => coordinator.Snapshot;
        public event EventHandler<PlaybackSnapshot>? SnapshotChanged
        {
            add => coordinator.SnapshotChanged += value;
            remove => coordinator.SnapshotChanged -= value;
        }
        public Task PauseAsync() => coordinator.PauseAsync();
        public Task ResumeAsync() => coordinator.ResumeAsync();
        public Task NextAsync() => coordinator.NextAsync();
        public Task PreviousAsync() => coordinator.PreviousAsync();
        public Task StopAsync() => coordinator.StopAsync();
    }
}
