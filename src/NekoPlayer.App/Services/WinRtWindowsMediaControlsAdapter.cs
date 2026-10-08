using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace NekoPlayer.App.Services;

/// <summary>Uses the official desktop WinRT projection; it does not create another audio player.</summary>
internal sealed class WinRtWindowsMediaControlsAdapter : IWindowsMediaControlsAdapter
{
    private readonly object _gate = new();
    private readonly Action<Action> _post;
    private SystemMediaTransportControls? _controls;
    private SystemMediaControlSnapshot? _metadata;
    private long _metadataVersion;

    public WinRtWindowsMediaControlsAdapter(IntPtr hwnd, Action<Action> post)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Windows SMTC requires Windows 10 1809 or later.");
        _post = post;
        // This is the main Avalonia HWND, retained while that window is hidden to the tray.
        var controls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        try
        {
            // Do not expose an empty active session before the coordinator's metadata is published.
            controls.IsEnabled = false;
            controls.ButtonPressed += OnButtonPressed;
            _controls = controls;
        }
        catch
        {
            try { controls.IsEnabled = false; } catch { }
            throw;
        }
    }

    public event EventHandler<SystemMediaControlButton>? ButtonPressed;

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_controls is null) return;
            _controls.IsEnabled = enabled;
            if (!enabled) _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        }
    }

    public void Update(SystemMediaControlSnapshot snapshot)
    {
        string? cover;
        long version;
        lock (_gate)
        {
            if (_controls is null) return;
            _controls.IsPlayEnabled = snapshot.HasTrack;
            _controls.IsPauseEnabled = snapshot.HasTrack;
            _controls.IsNextEnabled = snapshot.HasTrack;
            _controls.IsPreviousEnabled = snapshot.HasTrack;
            _controls.IsStopEnabled = snapshot.HasTrack;
            _controls.PlaybackStatus = snapshot.Status switch
            {
                SystemMediaPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
                SystemMediaPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
                SystemMediaPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
                SystemMediaPlaybackStatus.Changing => MediaPlaybackStatus.Changing,
                _ => MediaPlaybackStatus.Closed
            };
            if (_metadata is { } current && current.SessionId == snapshot.SessionId && current.Title == snapshot.Title
                && current.Artist == snapshot.Artist && current.Album == snapshot.Album && current.CoverCachePath == snapshot.CoverCachePath)
                return;
            _metadata = snapshot;
            version = ++_metadataVersion;
            var updater = _controls.DisplayUpdater;
            updater.ClearAll(); // A cover from the previous session must never leak into the next song.
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = snapshot.Title;
            updater.MusicProperties.Artist = snapshot.Artist;
            updater.MusicProperties.AlbumTitle = snapshot.Album;
            updater.Update();
            cover = snapshot.CoverCachePath;
        }
        if (cover is not null) _ = LoadCachedCoverAsync(cover, version);
    }

    private async Task LoadCachedCoverAsync(string path, long version)
    {
        try
        {
            // Read only the existing cache file. SMTC never fetches remote artwork.
            var file = await StorageFile.GetFileFromPathAsync(path);
            var thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            _post(() =>
            {
                try
                {
                    lock (_gate)
                    {
                        if (_controls is null || version != _metadataVersion) return;
                        _controls.DisplayUpdater.Thumbnail = thumbnail;
                        _controls.DisplayUpdater.Update();
                    }
                }
                catch { /* A missing/invalid cached image cannot disable playback or media keys. */ }
            });
        }
        catch { /* Cache eviction and artwork read failure leave the metadata without a thumbnail. */ }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var button = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => SystemMediaControlButton.Play,
            SystemMediaTransportControlsButton.Pause => SystemMediaControlButton.Pause,
            SystemMediaTransportControlsButton.Next => SystemMediaControlButton.Next,
            SystemMediaTransportControlsButton.Previous => SystemMediaControlButton.Previous,
            SystemMediaTransportControlsButton.Stop => SystemMediaControlButton.Stop,
            _ => (SystemMediaControlButton?)null
        };
        if (button is { } supported) ButtonPressed?.Invoke(this, supported);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_controls is not { } controls) return;
            _controls = null;
            _metadata = null;
            _metadataVersion++;
            // Event removal and disabling are each attempted even if Windows is shutting down.
            try { controls.ButtonPressed -= OnButtonPressed; } catch { }
            try { controls.PlaybackStatus = MediaPlaybackStatus.Closed; } catch { }
            try { controls.IsEnabled = false; } catch { }
            try { controls.DisplayUpdater.ClearAll(); controls.DisplayUpdater.Update(); } catch { }
        }
    }
}
