using NekoPlayer.App.Services;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Tests;

public sealed class WindowsMediaControlsTests
{
    [Theory]
    [InlineData(PlaybackState.Idle, SystemMediaPlaybackStatus.Closed)]
    [InlineData(PlaybackState.Playing, SystemMediaPlaybackStatus.Playing)]
    [InlineData(PlaybackState.Paused, SystemMediaPlaybackStatus.Paused)]
    [InlineData(PlaybackState.Stopped, SystemMediaPlaybackStatus.Stopped)]
    [InlineData(PlaybackState.Error, SystemMediaPlaybackStatus.Stopped)]
    [InlineData(PlaybackState.Loading, SystemMediaPlaybackStatus.Changing)]
    [InlineData(PlaybackState.Buffering, SystemMediaPlaybackStatus.Changing)]
    [InlineData(PlaybackState.Seeking, SystemMediaPlaybackStatus.Changing)]
    public void PlaybackStatesMapToOneWindowsStatus(PlaybackState state, SystemMediaPlaybackStatus expected)
    {
        Assert.Equal(expected, WindowsMediaControlsService.MapPlaybackStatus(Snapshot(1, state)));
    }

    [Fact]
    public void PendingSwitchIsChangingAndKeepsCurrentSongMetadata()
    {
        using var fixture = new Fixture();
        fixture.Playback.Publish(Snapshot(1, PlaybackState.Playing) with { PendingTrack = new Track { Title = "pending" } });
        fixture.Drain();
        var metadata = Assert.Single(fixture.Adapter.Updates);
        Assert.Equal(SystemMediaPlaybackStatus.Changing, metadata.Status);
        Assert.Equal("current", metadata.Title);
        Assert.Equal("artist", metadata.Artist);
        Assert.Equal("album", metadata.Album);
    }

    [Fact]
    public void PublishesOnlyExistingCachedArtwork()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            using var fixture = new Fixture();
            var snapshot = Snapshot(1, PlaybackState.Playing);
            snapshot.Track!.CoverCachePath = path;
            snapshot.Track.CoverUrl = "https://example.invalid/remote-cover.png";
            fixture.Playback.Publish(snapshot);
            fixture.Drain();
            Assert.Equal(Path.GetFullPath(path), Assert.Single(fixture.Adapter.Updates).CoverCachePath);
            File.Delete(path);
            fixture.Playback.Publish(snapshot with { State = PlaybackState.Paused });
            fixture.Drain();
            Assert.Null(fixture.Adapter.Updates.Last().CoverCachePath);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(SystemMediaControlButton.Play, "resume")]
    [InlineData(SystemMediaControlButton.Pause, "pause")]
    [InlineData(SystemMediaControlButton.Next, "next")]
    [InlineData(SystemMediaControlButton.Previous, "previous")]
    [InlineData(SystemMediaControlButton.Stop, "stop")]
    public void HardwareButtonsDelegateToExplicitCoordinatorActions(SystemMediaControlButton button, string expected)
    {
        using var fixture = new Fixture();
        fixture.Drain();
        fixture.Adapter.Press(button);
        Assert.Empty(fixture.Playback.Commands);
        fixture.Drain();
        Assert.Equal(expected, Assert.Single(fixture.Playback.Commands));
    }

    [Fact]
    public void ButtonQueuedForPreviousSessionCannotControlReplacementSong()
    {
        using var fixture = new Fixture();
        fixture.Drain();
        fixture.Adapter.Press(SystemMediaControlButton.Pause);
        fixture.Playback.Publish(Snapshot(2, PlaybackState.Playing, "replacement"));
        fixture.Drain();
        Assert.Empty(fixture.Playback.Commands);
        Assert.Equal("replacement", fixture.Adapter.Updates.Last().Title);
    }

    [Fact]
    public void OldSnapshotCannotReplaceNewSessionAndUpdatesAreCoalesced()
    {
        using var fixture = new Fixture();
        fixture.Playback.Publish(Snapshot(2, PlaybackState.Playing, "replacement"));
        fixture.Playback.Raise(Snapshot(1, PlaybackState.Error, "stale"));
        fixture.Drain();
        var snapshot = Assert.Single(fixture.Adapter.Updates);
        Assert.Equal(2, snapshot.SessionId);
        Assert.Equal("replacement", snapshot.Title);
        Assert.Equal(SystemMediaPlaybackStatus.Playing, snapshot.Status);
    }

    [Fact]
    public void DisabledSettingRejectsPendingButtonsAndReenableUsesLatestSession()
    {
        using var fixture = new Fixture();
        fixture.Drain();
        fixture.Adapter.Press(SystemMediaControlButton.Next);
        fixture.Service.IsEnabled = false;
        fixture.Playback.Publish(Snapshot(2, PlaybackState.Paused, "latest"));
        fixture.Drain();
        Assert.Empty(fixture.Playback.Commands);
        Assert.False(fixture.Adapter.Enabled);
        Assert.Equal(WindowsMediaControlsStatus.Disabled, fixture.Service.Status);
        fixture.Adapter.Press(SystemMediaControlButton.Play);
        fixture.Service.IsEnabled = true;
        fixture.Drain();
        Assert.True(fixture.Adapter.Enabled);
        Assert.Equal("latest", fixture.Adapter.Updates.Last().Title);
        Assert.Equal(SystemMediaPlaybackStatus.Paused, fixture.Adapter.Updates.Last().Status);
        Assert.Empty(fixture.Playback.Commands);
    }

    [Fact]
    public void DisposingUnsubscribesAndRejectsAlreadyQueuedEvents()
    {
        var fixture = new Fixture();
        fixture.Drain();
        fixture.Adapter.Press(SystemMediaControlButton.Stop);
        fixture.Service.Dispose();
        fixture.Service.Dispose();
        fixture.Playback.Publish(Snapshot(2, PlaybackState.Playing));
        fixture.Adapter.Press(SystemMediaControlButton.Play);
        fixture.Drain();
        Assert.Empty(fixture.Playback.Commands);
        Assert.True(fixture.Adapter.Disposed);
        Assert.Equal(0, fixture.Playback.SubscriberCount);
        Assert.Equal(0, fixture.Adapter.SubscriberCount);
        Assert.Equal(WindowsMediaControlsStatus.Disposed, fixture.Service.Status);
        Assert.False(fixture.Service.IsAvailable);
    }

    [Fact]
    public void UnsupportedWindowsActivationFailsWithoutCallingPlayback()
    {
        var playback = new FakePlayback();
        using var service = new WindowsMediaControlsService(playback,
            _ => throw new PlatformNotSupportedException("unavailable"), action => action());
        Assert.False(service.Initialize(new IntPtr(1)));
        Assert.Equal(WindowsMediaControlsStatus.Unavailable, service.Status);
        Assert.Contains("仍可正常播放", service.StatusText);
        playback.Publish(Snapshot(2, PlaybackState.Playing));
        Assert.Empty(playback.Commands);
    }

    [Fact]
    public void AdapterFailureDisablesIntegrationAndAllowsExplicitRetry()
    {
        var playback = new FakePlayback();
        var adapter = new FakeAdapter { ThrowOnUpdate = true };
        using var service = new WindowsMediaControlsService(playback, _ => adapter, action => action());
        Assert.False(service.Initialize(new IntPtr(1)));
        Assert.Equal(WindowsMediaControlsStatus.Unavailable, service.Status);
        Assert.True(adapter.Disposed);
        Assert.Empty(playback.Commands);
        adapter = new FakeAdapter();
        Assert.True(service.Initialize(new IntPtr(1)));
        Assert.Equal(WindowsMediaControlsStatus.Available, service.Status);
        Assert.Single(adapter.Updates);
    }

    [Fact]
    public void DispatcherFailureCannotEscapeIntoPlaybackSnapshotPublisher()
    {
        var playback = new FakePlayback();
        var adapter = new FakeAdapter();
        using var service = new WindowsMediaControlsService(playback, _ => adapter,
            _ => throw new InvalidOperationException("dispatcher unavailable"));
        Assert.False(service.Initialize(new IntPtr(1)));
        playback.Publish(Snapshot(2, PlaybackState.Playing));
        Assert.Equal(WindowsMediaControlsStatus.Unavailable, service.Status);
        Assert.True(adapter.Disposed);
    }

    private static PlaybackSnapshot Snapshot(long id, PlaybackState state, string title = "current") =>
        new(id, new Track { Title = title, Artist = "artist", Album = "album" }, null,
            state, TimeSpan.Zero, TimeSpan.FromMinutes(3), true);

    private sealed class Fixture : IDisposable
    {
        private readonly Queue<Action> _dispatch = new();
        public FakePlayback Playback { get; } = new();
        public FakeAdapter Adapter { get; } = new();
        public WindowsMediaControlsService Service { get; }

        public Fixture()
        {
            Service = new(Playback, _ => Adapter, callback => _dispatch.Enqueue(callback));
            Assert.True(Service.IsEnabled);
            Assert.True(Service.Initialize(new IntPtr(1)));
        }

        public void Drain()
        {
            while (_dispatch.TryDequeue(out var callback)) callback();
        }

        public void Dispose() => Service.Dispose();
    }

    private sealed class FakePlayback : IWindowsMediaPlaybackController
    {
        private EventHandler<PlaybackSnapshot>? _changed;
        public PlaybackSnapshot Snapshot { get; private set; } = WindowsMediaControlsTests.Snapshot(1, PlaybackState.Playing);
        public List<string> Commands { get; } = [];
        public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;
        public event EventHandler<PlaybackSnapshot>? SnapshotChanged
        {
            add => _changed += value;
            remove => _changed -= value;
        }
        public void Publish(PlaybackSnapshot snapshot) { Snapshot = snapshot; Raise(snapshot); }
        public void Raise(PlaybackSnapshot snapshot) => _changed?.Invoke(this, snapshot);
        private Task Record(string command) { Commands.Add(command); return Task.CompletedTask; }
        public Task PauseAsync() => Record("pause");
        public Task ResumeAsync() => Record("resume");
        public Task NextAsync() => Record("next");
        public Task PreviousAsync() => Record("previous");
        public Task StopAsync() => Record("stop");
    }

    private sealed class FakeAdapter : IWindowsMediaControlsAdapter
    {
        private EventHandler<SystemMediaControlButton>? _pressed;
        public bool Enabled { get; private set; }
        public bool Disposed { get; private set; }
        public bool ThrowOnUpdate { get; init; }
        public List<SystemMediaControlSnapshot> Updates { get; } = [];
        public int SubscriberCount => _pressed?.GetInvocationList().Length ?? 0;
        public event EventHandler<SystemMediaControlButton>? ButtonPressed
        {
            add => _pressed += value;
            remove => _pressed -= value;
        }
        public void Press(SystemMediaControlButton button) => _pressed?.Invoke(this, button);
        public void SetEnabled(bool enabled) => Enabled = enabled;
        public void Update(SystemMediaControlSnapshot snapshot)
        {
            if (ThrowOnUpdate) throw new InvalidOperationException("WinRT disconnected");
            Updates.Add(snapshot);
        }
        public void Dispose() { Disposed = true; Enabled = false; }
    }
}
