using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;

namespace NekoPlayer.Tests;

public sealed class CoordinatorBehaviorTests
{
    [Fact]
    public async Task StopDuringResolutionCannotStartOrReplaceQueueLater()
    {
        await using var fixture = new Fixture();
        var old = NewTrack("old");
        var target = NewTrack("pending");
        fixture.Queue.Replace([old]);
        var delayed = NewCompletion<PlaybackResolution>();
        fixture.Online.ResolveHook = (_, _) => delayed.Task;
        var request = fixture.Coordinator.PlayAsync(target, [target]);
        await fixture.Coordinator.StopAsync();
        delayed.SetResult(Full(target));
        Assert.False((await request).Started);
        Assert.Empty(fixture.Audio.Loads);
        Assert.Equal(old.Id, Assert.Single(fixture.Queue.Items).Id);
        Assert.Equal(PlaybackState.Stopped, fixture.Coordinator.Snapshot.State);
    }

    [Fact]
    public async Task LaterRequestWinsEvenWhenOldProviderIgnoresCancellation()
    {
        await using var fixture = new Fixture();
        var a = NewTrack("A"); var b = NewTrack("B");
        var delayed = NewCompletion<PlaybackResolution>();
        fixture.Online.ResolveHook = (track, _) => track.Id == a.Id ? delayed.Task : Task.FromResult(Full(track));
        var first = fixture.Coordinator.PlayAsync(a, [a]);
        var second = await fixture.Coordinator.PlayAsync(b, [b]);
        delayed.SetResult(Full(a));
        Assert.False((await first).Started);
        Assert.True(second.Started);
        Assert.Equal(b.Id, fixture.Coordinator.Snapshot.Track!.Id);
        Assert.Equal(b.Id, fixture.Queue.Current!.Id);
        Assert.Single(fixture.Audio.Loads);
    }

    [Fact]
    public async Task OldLoadingRequestCannotPlayAfterStop()
    {
        await using var fixture = new Fixture();
        var entered = NewCompletion<bool>(); var release = NewCompletion<bool>();
        fixture.Audio.LoadHook = async (_, _) => { entered.TrySetResult(true); await release.Task; };
        var request = fixture.Coordinator.PlayAsync(NewTrack("A"));
        await entered.Task;
        var stop = fixture.Coordinator.StopAsync();
        release.SetResult(true);
        await stop;
        Assert.False((await request).Started);
        Assert.Equal(0, fixture.Audio.PlayCalls);
        Assert.Null(fixture.Audio.LoadedSource);
        Assert.Equal(PlaybackState.Stopped, fixture.Coordinator.Snapshot.State);
    }

    [Fact]
    public async Task StaleStatePositionFailureAndCompletionCannotTouchNewSession()
    {
        await using var fixture = new Fixture();
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C");
        var first = await fixture.Coordinator.PlayAsync(a, [a, b, c]);
        var delayed = NewCompletion<PlaybackResolution>();
        fixture.Online.ResolveHook = (track, _) => track.Id == b.Id ? delayed.Task : Task.FromResult(Full(track));
        var request = fixture.Coordinator.PlayAsync(b);
        fixture.Audio.Complete(first.SessionId);
        Assert.Single(fixture.Audio.Loads);
        delayed.SetResult(Full(b));
        var second = await request;
        fixture.Audio.EmitState(first.SessionId, PlaybackState.Error);
        fixture.Audio.EmitPosition(first.SessionId, TimeSpan.FromSeconds(99));
        fixture.Audio.Fail(first.SessionId);
        fixture.Audio.Complete(first.SessionId);
        Assert.Equal(second.SessionId, fixture.Coordinator.CurrentSessionId);
        Assert.Equal(b.Id, fixture.Coordinator.Snapshot.Track!.Id);
        Assert.Equal(TimeSpan.Zero, fixture.Coordinator.Snapshot.Position);
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
        Assert.Equal(2, fixture.Audio.Loads.Count);
    }

    [Fact]
    public async Task QueueSourceIsCapturedAndOnlineContextSavedBeforeResolve()
    {
        await using var fixture = new Fixture();
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C");
        var context = new List<Track> { a, b };
        var delayed = NewCompletion<PlaybackResolution>();
        fixture.Online.ResolveHook = (_, _) =>
        {
            Assert.Equal(new[] { a.Id, b.Id }, Assert.Single(fixture.Catalog.Batches).Select(x => x.Id));
            return delayed.Task;
        };
        var request = fixture.Coordinator.PlayAsync(b, context);
        context.Clear(); context.Add(c);
        delayed.SetResult(Full(b));
        Assert.True((await request).Started);
        Assert.Equal(new[] { a.Id, b.Id }, fixture.Queue.Items.Select(x => x.Id));
        Assert.Equal(b.Id, fixture.Queue.Current!.Id);
    }

    [Fact]
    public async Task TargetAlreadyInQueuePreservesExistingQueueAndSelectsByIdentity()
    {
        await using var fixture = new Fixture();
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C");
        fixture.Queue.Replace([a, b]);
        Assert.True((await fixture.Coordinator.PlayAsync(b, [c, b])).Started);
        Assert.Equal(new[] { a.Id, b.Id }, fixture.Queue.Items.Select(x => x.Id));
        Assert.Equal(b.Id, fixture.Queue.Current!.Id);
    }

    [Fact]
    public async Task FailedResolutionPreservesPreviouslyPlayingTrackAndQueue()
    {
        await using var fixture = new Fixture();
        var a = NewTrack("A"); var b = NewTrack("B");
        var first = await fixture.Coordinator.PlayAsync(a);
        fixture.Online.ResolveHook = (_, _) => Task.FromResult(new PlaybackResolution(MusicAvailability.Unavailable, null, "restricted"));
        Assert.False((await fixture.Coordinator.PlayAsync(b, [b])).Started);
        Assert.Equal(first.SessionId, fixture.Coordinator.CurrentSessionId);
        Assert.Equal(a.Id, fixture.Coordinator.Snapshot.Track!.Id);
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
        Assert.Equal(a.Id, Assert.Single(fixture.Queue.Items).Id);
        fixture.Audio.EmitPosition(first.SessionId, TimeSpan.FromSeconds(12));
        Assert.Equal(TimeSpan.FromSeconds(12), fixture.Coordinator.Snapshot.Position);
    }

    [Fact]
    public async Task DecodeFailureAfterSelectionShowsTargetAsRetryableError()
    {
        await using var fixture = new Fixture();
        var a = NewTrack("A"); var b = NewTrack("B");
        await fixture.Coordinator.PlayAsync(a);
        fixture.Audio.LoadHook = (_, _) => throw new InvalidOperationException("decode failed");
        Assert.False((await fixture.Coordinator.PlayAsync(b, [b])).Started);
        Assert.Equal(b.Id, fixture.Coordinator.Snapshot.Track!.Id);
        Assert.Equal(b.Id, fixture.Queue.Current!.Id);
        Assert.Equal(PlaybackState.Error, fixture.Coordinator.Snapshot.State);
        fixture.Audio.LoadHook = null;
        await fixture.Coordinator.ToggleAsync();
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
        Assert.Equal(b.Id, fixture.Coordinator.Snapshot.Track!.Id);
    }

    [Fact]
    public async Task PreviewNeedsExplicitPermissionAndCompletionStopsInRepeatOne()
    {
        await using var fixture = new Fixture();
        var preview = NewTrack("preview"); var full = NewTrack("full");
        fixture.Queue.PlayMode = PlayMode.RepeatOne;
        fixture.Online.ResolveHook = (track, _) => Task.FromResult(track.Id == preview.Id
            ? new PlaybackResolution(MusicAvailability.Preview, Full(track).Source, "preview only") : Full(track));
        Assert.False((await fixture.Coordinator.PlayAsync(preview, [preview, full])).Started);
        Assert.Empty(fixture.Audio.Loads);
        var result = await fixture.Coordinator.PlayAsync(preview, [preview, full], preview: true);
        Assert.True(result.Started);
        Assert.True(fixture.Coordinator.Snapshot.IsPreview);
        fixture.Audio.Complete(result.SessionId, isPreview: false);
        await UntilAsync(() => fixture.Audio.LoadedSource is null);
        Assert.Equal(PlaybackState.Stopped, fixture.Coordinator.Snapshot.State);
        Assert.Single(fixture.Audio.Loads);
        Assert.Equal(preview.Id, fixture.Queue.Current!.Id);
    }

    [Fact]
    public async Task NextSkipsPreviewAndUnavailableAndStopsAfterOneQueuePass()
    {
        await using var fixture = new Fixture();
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C"); var d = NewTrack("D");
        b.Availability = MusicAvailability.Preview;
        c.Availability = MusicAvailability.Unavailable;
        await fixture.Coordinator.PlayAsync(a, [a, b, c, d]);
        fixture.Online.ResolveHook = (_, _) => Task.FromResult(new PlaybackResolution(MusicAvailability.Unavailable, null));
        var before = fixture.Online.ResolveCalls;
        await fixture.Coordinator.NextAsync();
        Assert.InRange(fixture.Online.ResolveCalls - before, 1, 4);
        Assert.Equal(PlaybackState.Stopped, fixture.Coordinator.Snapshot.State);
        Assert.Single(fixture.Audio.Loads);
        Assert.NotNull(fixture.Coordinator.Snapshot.Message);
    }

    [Fact]
    public async Task FullCompletionAdvancesAndPublishesSuccessfulStartOnce()
    {
        await using var fixture = new Fixture();
        var a = NewTrack("A"); var b = NewTrack("B");
        var starts = new List<PlaybackRequestResult>();
        fixture.Coordinator.PlaybackStarted += (_, result) => starts.Add(result);
        var first = await fixture.Coordinator.PlayAsync(a, [a, b]);
        fixture.Audio.Complete(first.SessionId);
        await UntilAsync(() => fixture.Coordinator.Snapshot.Track?.Id == b.Id && fixture.Coordinator.Snapshot.State == PlaybackState.Playing);
        Assert.Equal(new[] { a.Id, b.Id }, starts.Select(x => x.Track!.Id));
        fixture.Audio.Complete(first.SessionId);
        Assert.Equal(2, fixture.Audio.Loads.Count);
    }

    [Fact]
    public async Task FullCompletionInRepeatOneRestartsCurrentAndSequentialEndStops()
    {
        await using var fixture = new Fixture();
        fixture.Queue.PlayMode = PlayMode.RepeatOne;
        var a = NewTrack("A");
        var first = await fixture.Coordinator.PlayAsync(a);
        fixture.Audio.Complete(first.SessionId);
        await UntilAsync(() => fixture.Audio.Loads.Count == 2 && fixture.Coordinator.Snapshot.State == PlaybackState.Playing);
        Assert.Equal(a.Id, fixture.Coordinator.Snapshot.Track!.Id);
        fixture.Queue.PlayMode = PlayMode.Sequential;
        fixture.Audio.Complete(fixture.Coordinator.CurrentSessionId);
        await UntilAsync(() => fixture.Coordinator.Snapshot.State == PlaybackState.Stopped);
        Assert.Equal(2, fixture.Audio.Loads.Count);
    }

    [Fact]
    public async Task SeekCompletingAfterNewIntentCannotMoveNewTrack()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Coordinator.PlayAsync(NewTrack("A"));
        var entered = NewCompletion<bool>(); var release = NewCompletion<bool>();
        fixture.Audio.SeekHook = async (_, _) => { entered.TrySetResult(true); await release.Task; };
        var seek = fixture.Coordinator.SeekAsync(TimeSpan.FromSeconds(40));
        await entered.Task;
        var b = NewTrack("B");
        var next = fixture.Coordinator.PlayAsync(b);
        release.SetResult(true);
        await seek;
        Assert.True((await next).Started);
        fixture.Audio.EmitPosition(first.SessionId, TimeSpan.FromSeconds(40));
        Assert.Equal(b.Id, fixture.Coordinator.Snapshot.Track!.Id);
        Assert.Equal(TimeSpan.Zero, fixture.Coordinator.Snapshot.Position);
    }

    [Fact]
    public async Task ExternalCancellationBeforeSwitchPreservesOldPlaybackAndControls()
    {
        await using var fixture = new Fixture();
        var a = NewTrack("A"); var b = NewTrack("B");
        var first = await fixture.Coordinator.PlayAsync(a);
        var delayed = NewCompletion<PlaybackResolution>();
        fixture.Online.ResolveHook = (_, _) => delayed.Task;
        using var cancellation = new CancellationTokenSource();
        var request = fixture.Coordinator.PlayAsync(b, [b], cancellationToken: cancellation.Token);
        cancellation.Cancel();
        delayed.SetResult(Full(b));
        Assert.False((await request).Started);
        Assert.Null(fixture.Coordinator.Snapshot.PendingTrack);
        Assert.Equal(first.SessionId, fixture.Coordinator.CurrentSessionId);
        Assert.Equal(a.Id, fixture.Coordinator.Snapshot.Track!.Id);
        await fixture.Coordinator.ToggleAsync();
        Assert.Equal(PlaybackState.Paused, fixture.Coordinator.Snapshot.State);
    }

    [Fact]
    public async Task SeekIntentSurvivesFailureDuringSeekAndUrlRecovery()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Coordinator.PlayAsync(NewTrack("A"));
        var intent = fixture.Coordinator.CurrentIntentId;
        var entered = NewCompletion<bool>(); var release = NewCompletion<bool>();
        fixture.Audio.SeekHook = async (_, _) => { entered.TrySetResult(true); await release.Task; };
        var seek = fixture.Coordinator.SeekAsync(TimeSpan.FromSeconds(40));
        await entered.Task;
        fixture.Audio.Fail(first.SessionId);
        release.SetResult(true);
        await seek;
        await UntilAsync(() => fixture.Audio.Loads.Count == 2 && fixture.Coordinator.Snapshot.State == PlaybackState.Playing);
        Assert.Equal(intent, fixture.Coordinator.CurrentIntentId);
        Assert.Equal(TimeSpan.FromSeconds(40), fixture.Audio.Loads[1].Position);
        Assert.Equal(TimeSpan.FromSeconds(40), fixture.Coordinator.Snapshot.Position);
    }

    [Fact]
    public async Task VeryShortPreviewCompletionIsDeliveredAfterSuccessfulStart()
    {
        await using var fixture = new Fixture();
        var track = NewTrack("very short preview");
        fixture.Online.ResolveHook = (_, _) => Task.FromResult(new PlaybackResolution(MusicAvailability.Preview,
            new AudioSource("https://music.invalid/preview", true, TimeSpan.FromMilliseconds(1), IsPreview: true)));
        fixture.Audio.PlayedHook = session => fixture.Audio.Complete(session, isPreview: true);
        var started = 0;
        fixture.Coordinator.PlaybackStarted += (_, _) => started++;
        Assert.True((await fixture.Coordinator.PlayAsync(track, preview: true)).Started);
        Assert.Equal(1, started);
        Assert.Equal(PlaybackState.Stopped, fixture.Coordinator.Snapshot.State);
        Assert.Null(fixture.Audio.LoadedSource);
        Assert.Single(fixture.Audio.Loads);
    }

    [Fact]
    public async Task RemovingActiveItemUnloadsAndPreservesOtherItemsWithoutStartingThem()
    {
        await using var fixture = new Fixture();
        var a = NewTrack("A"); var b = NewTrack("B");
        await fixture.Coordinator.PlayAsync(a, [a, b]);
        await fixture.Coordinator.RemoveQueueItemAsync(a.Id);
        Assert.Equal(b.Id, Assert.Single(fixture.Queue.Items).Id);
        Assert.Null(fixture.Queue.Current);
        Assert.Null(fixture.Coordinator.Snapshot.Track);
        Assert.Null(fixture.Audio.LoadedSource);
        Assert.Single(fixture.Audio.Loads);
    }

    [Fact]
    public async Task LyricsDoNotBlockPlaybackAndTheirFailureDoesNotChangeAudioState()
    {
        await using var fixture = new Fixture();
        var lyrics = NewCompletion<IReadOnlyList<LyricsLine>>();
        fixture.Online.LyricsHook = (_, _) => lyrics.Task;
        Assert.True((await fixture.Coordinator.PlayAsync(NewTrack("A"))).Started);
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
        lyrics.SetException(new InvalidOperationException("lyrics offline"));
        await UntilAsync(() => fixture.Coordinator.LyricsMessage is not null);
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
        Assert.Contains("lyrics offline", fixture.Coordinator.Snapshot.Message!);
    }

    [Fact]
    public async Task PreviewLyricsUseSegmentClockWithKnownOffsetAndOnlySegmentLines()
    {
        await using var fixture = new Fixture();
        var track = NewTrack("preview"); track.Duration = TimeSpan.FromMinutes(4);
        fixture.Online.ResolveHook = (_, _) => Task.FromResult(new PlaybackResolution(MusicAvailability.Preview,
            new AudioSource("https://music.invalid/preview", true, TimeSpan.FromSeconds(30),
                IsPreview: true, PreviewStart: TimeSpan.FromSeconds(90))));
        fixture.Online.LyricsHook = (_, _) => Task.FromResult<IReadOnlyList<LyricsLine>>([
            new(TimeSpan.FromSeconds(70), "line before segment"),
            new(TimeSpan.FromSeconds(100), "line in segment"),
            new(TimeSpan.FromSeconds(125), "line after segment")]);
        var delivered = NewCompletion<PlaybackLyrics>();
        fixture.Coordinator.LyricsChanged += (_, value) => { if (value.Lines.Count > 0) delivered.TrySetResult(value); };
        Assert.True((await fixture.Coordinator.PlayAsync(track, preview: true)).Started);
        var lyrics = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.Coordinator.Snapshot.Duration);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(10) }, lyrics.Lines.Select(x => x.Timestamp));
        Assert.Equal(new[] { "line before segment", "line in segment" }, lyrics.Lines.Select(x => x.Text));
    }

    [Fact]
    public async Task PreviewLyricsWithUnknownOffsetKeepProviderTimestamps()
    {
        await using var fixture = new Fixture();
        var track = NewTrack("unknown offset");
        fixture.Online.ResolveHook = (_, _) => Task.FromResult(new PlaybackResolution(MusicAvailability.Preview,
            new AudioSource("https://music.invalid/preview", true, TimeSpan.FromSeconds(30), IsPreview: true)));
        fixture.Online.LyricsHook = (_, _) => Task.FromResult<IReadOnlyList<LyricsLine>>([new(TimeSpan.FromSeconds(90), "original")]);
        var delivered = NewCompletion<PlaybackLyrics>();
        fixture.Coordinator.LyricsChanged += (_, value) => { if (value.Lines.Count > 0) delivered.TrySetResult(value); };
        Assert.True((await fixture.Coordinator.PlayAsync(track, preview: true)).Started);
        Assert.Equal(TimeSpan.FromSeconds(90), (await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5))).Lines[0].Timestamp);
    }

    [Fact]
    public async Task LyricsFromOldSessionAreDiscardedEvenIfCancellationIsIgnored()
    {
        await using var fixture = new Fixture();
        var a = NewTrack("A"); var b = NewTrack("B");
        var delayed = NewCompletion<IReadOnlyList<LyricsLine>>();
        fixture.Online.LyricsHook = (track, _) => track.Id == a.Id ? delayed.Task
            : Task.FromResult<IReadOnlyList<LyricsLine>>([new(TimeSpan.Zero, "B lyrics")]);
        var received = new List<PlaybackLyrics>();
        fixture.Coordinator.LyricsChanged += (_, lyrics) => { lock (received) received.Add(lyrics); };
        await fixture.Coordinator.PlayAsync(a);
        await UntilAsync(() => fixture.Online.LyricsCalls > 0);
        await fixture.Coordinator.PlayAsync(b);
        delayed.SetResult([new(TimeSpan.Zero, "A lyrics")]);
        await UntilAsync(() => { lock (received) return received.Any(x => x.Lines.Any(l => l.Text == "B lyrics")); });
        lock (received) Assert.DoesNotContain(received, x => x.Lines.Any(l => l.Text == "A lyrics"));
    }

    [Fact]
    public async Task RemoteRecoveryResolvesOnceAndRejectsOldFailureAndEof()
    {
        await using var fixture = new Fixture();
        var starts = 0;
        fixture.Coordinator.PlaybackStarted += (_, _) => starts++;
        var first = await fixture.Coordinator.PlayAsync(NewTrack("A"));
        var intent = fixture.Coordinator.CurrentIntentId;
        fixture.Audio.EmitPosition(first.SessionId, TimeSpan.FromSeconds(12));
        fixture.Audio.Fail(first.SessionId);
        await UntilAsync(() => fixture.Audio.Loads.Count == 2 && fixture.Coordinator.Snapshot.State == PlaybackState.Playing);
        var recovered = fixture.Coordinator.CurrentSessionId;
        Assert.NotEqual(first.SessionId, recovered);
        Assert.Equal(intent, fixture.Coordinator.CurrentIntentId);
        Assert.Equal(TimeSpan.FromSeconds(12), fixture.Audio.Loads[1].Position);
        fixture.Audio.Fail(first.SessionId);
        fixture.Audio.Complete(first.SessionId);
        fixture.Audio.Fail(recovered);
        Assert.Equal(2, fixture.Online.ResolveCalls);
        Assert.Equal(2, fixture.Audio.Loads.Count);
        Assert.Equal(1, starts);
        Assert.Equal(PlaybackState.Error, fixture.Coordinator.Snapshot.State);
    }

    [Fact]
    public async Task StopWhileRefreshingUrlCannotRevivePlayback()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Coordinator.PlayAsync(NewTrack("A"));
        var delayed = NewCompletion<PlaybackResolution>();
        fixture.Online.ResolveHook = (_, _) => delayed.Task;
        fixture.Audio.Fail(first.SessionId);
        await fixture.Coordinator.StopAsync();
        delayed.SetResult(Full(first.Track!));
        await Task.Yield();
        Assert.Single(fixture.Audio.Loads);
        Assert.Null(fixture.Audio.LoadedSource);
        Assert.Equal(PlaybackState.Stopped, fixture.Coordinator.Snapshot.State);
    }

    private static Track NewTrack(string title) => new()
    {
        Id = Guid.NewGuid(), Title = title, SourceKind = TrackSourceKind.Online,
        ProviderId = "test", ProviderTrackId = title, Duration = TimeSpan.FromMinutes(3)
    };

    private static PlaybackResolution Full(Track track) => new(MusicAvailability.Full,
        new AudioSource("https://music.invalid/" + track.ProviderTrackId, true, track.Duration));
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeSessionAudio Audio { get; } = new();
        public PlaybackQueueService Queue { get; } = new();
        public FakeOnline Online { get; } = new();
        public FakeCatalog Catalog { get; } = new();
        public PlaybackCoordinator Coordinator { get; }
        public Fixture() => Coordinator = new(Audio, Queue, Online, new LrcParser(), Catalog);
        public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
    }

    private sealed class FakeOnline : IOnlineMusicService
    {
        private int _resolveCalls;
        private int _lyricsCalls;
        public int ResolveCalls => Volatile.Read(ref _resolveCalls);
        public int LyricsCalls => Volatile.Read(ref _lyricsCalls);
        public Func<Track, CancellationToken, Task<PlaybackResolution>>? ResolveHook { get; set; }
        public Func<Track, CancellationToken, Task<IReadOnlyList<LyricsLine>>>? LyricsHook { get; set; }
        public IReadOnlyList<MusicProviderInfo> Providers => [];
        public Task<MusicSearchPage> SearchAsync(MusicSearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlaybackResolution> ResolveAsync(Track track, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _resolveCalls); return ResolveHook?.Invoke(track, cancellationToken) ?? Task.FromResult(Full(track)); }
        public Task<IReadOnlyList<LyricsLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _lyricsCalls); return LyricsHook?.Invoke(track, cancellationToken) ?? Task.FromResult<IReadOnlyList<LyricsLine>>([]); }
    }

    private sealed class FakeCatalog : ITrackCatalog
    {
        public List<Track[]> Batches { get; } = [];
        public Task<IReadOnlyList<Track>> GetTracksByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Track>>([]);
        public Task<Track> EnsureOnlineTrackAsync(Track track, CancellationToken cancellationToken = default) => Task.FromResult(track);
        public Task<IReadOnlyList<Track>> EnsureOnlineTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default)
        { var array = tracks.ToArray(); Batches.Add(array); return Task.FromResult<IReadOnlyList<Track>>(array); }
    }

    private sealed class FakeSessionAudio : ISessionAudioPlayerService
    {
        public PlaybackState State { get; private set; }
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration { get; private set; }
        public float Volume { get; set; } = 1;
        public bool IsMuted { get; set; }
        public long SessionId { get; private set; }
        public bool CanSeek => LoadedSource?.CanSeek ?? false;
        public AudioSource? LoadedSource { get; private set; }
        public List<(AudioSource Source, TimeSpan? Position)> Loads { get; } = [];
        public int PlayCalls { get; private set; }
        public Func<AudioSource, CancellationToken, Task>? LoadHook { get; set; }
        public Func<TimeSpan, CancellationToken, Task>? SeekHook { get; set; }
        public Action<long>? PlayedHook { get; set; }
        public event EventHandler<PlaybackState>? StateChanged;
        public event EventHandler<TimeSpan>? PositionChanged;
        public event EventHandler? PlaybackCompleted;
        public event EventHandler<Exception>? PlaybackFailed;
        public event EventHandler<AudioSessionState>? SessionStateChanged;
        public event EventHandler<AudioSessionPosition>? SessionPositionChanged;
        public event EventHandler<AudioSessionFailure>? SessionPlaybackFailed;
        public event EventHandler<AudioSessionCompletion>? SessionPlaybackCompleted;

        public Task LoadAsync(string filePath, TimeSpan? startPosition = null, CancellationToken cancellationToken = default)
            => LoadAsync(AudioSource.Local(filePath), startPosition, cancellationToken);
        public async Task LoadAsync(AudioSource source, TimeSpan? startPosition = null, CancellationToken cancellationToken = default)
        {
            if (LoadHook is not null) await LoadHook(source, cancellationToken);
            LoadedSource = source;
            SessionId = source.SessionId;
            Position = startPosition ?? TimeSpan.Zero;
            Duration = source.Duration ?? TimeSpan.FromMinutes(3);
            Loads.Add((source, startPosition));
            EmitState(SessionId, PlaybackState.Loading);
        }
        public Task PlayAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); PlayCalls++; EmitState(SessionId, PlaybackState.Playing); PlayedHook?.Invoke(SessionId); return Task.CompletedTask; }
        public Task PauseAsync() { EmitState(SessionId, PlaybackState.Paused); return Task.CompletedTask; }
        public Task StopAsync() { Position = TimeSpan.Zero; EmitState(SessionId, PlaybackState.Stopped); return Task.CompletedTask; }
        public Task UnloadAsync() { LoadedSource = null; Position = TimeSpan.Zero; EmitState(SessionId, PlaybackState.Idle); return Task.CompletedTask; }
        public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        { if (SeekHook is not null) await SeekHook(position, cancellationToken); Position = position; EmitPosition(SessionId, position); }
        public void EmitState(long session, PlaybackState state)
        { if (session == SessionId) State = state; StateChanged?.Invoke(this, state); SessionStateChanged?.Invoke(this, new(session, state)); }
        public void EmitPosition(long session, TimeSpan position)
        { if (session == SessionId) Position = position; PositionChanged?.Invoke(this, position); SessionPositionChanged?.Invoke(this, new(session, position)); }
        public void Fail(long session)
        { var error = new InvalidOperationException("expired source"); PlaybackFailed?.Invoke(this, error); SessionPlaybackFailed?.Invoke(this, new(session, error)); }
        public void Complete(long session, bool isPreview = false)
        { PlaybackCompleted?.Invoke(this, EventArgs.Empty); SessionPlaybackCompleted?.Invoke(this, new(session, isPreview)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
