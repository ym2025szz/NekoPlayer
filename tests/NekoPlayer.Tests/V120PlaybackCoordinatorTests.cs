using System.Collections.Concurrent;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;

namespace NekoPlayer.Tests;

public sealed class V120PlaybackCoordinatorTests
{
    [Fact]
    public async Task FullRepeatOneCompletesOneHundredRoundsWithFreshUrlsAndRejectsOldCompletions()
    {
        await using var fixture = new Fixture();
        var a = Track("A"); var b = Track("B");
        fixture.Queue.PlayMode = PlayMode.RepeatOne;
        var result = await fixture.Coordinator.PlayAsync(a, [a, b]);
        Assert.True(result.Started);
        var sessions = new HashSet<long> { result.SessionId };

        for (var round = 1; round <= 100; round++)
        {
            var oldSession = fixture.Coordinator.CurrentSessionId;
            fixture.Audio.NaturalComplete(oldSession);
            await UntilAsync(() => fixture.Audio.PlayCalls == round + 1 && fixture.Coordinator.Snapshot.State == PlaybackState.Playing);
            var current = fixture.Coordinator.CurrentSessionId;
            Assert.True(sessions.Add(current));
            fixture.Audio.Complete(oldSession);
            fixture.Audio.PositionEvent(oldSession, TimeSpan.FromSeconds(100));
            fixture.Audio.Fail(oldSession);
            Assert.Equal(current, fixture.Coordinator.CurrentSessionId);
            Assert.Equal(a.Id, fixture.Coordinator.Snapshot.Track!.Id);
            Assert.Equal(a.Id, fixture.Queue.Current!.Id);
            Assert.Equal(PlayMode.RepeatOne, fixture.Queue.PlayMode);
            Assert.Equal(TimeSpan.Zero, fixture.Coordinator.Snapshot.Position);
        }

        Assert.Equal(101, fixture.Online.ResolveCalls);
        Assert.Equal(101, fixture.Audio.LoadAttempts);
        Assert.Equal(101, fixture.Audio.AttemptedSources.Select(x => x.Input).Distinct().Count());
        Assert.All(fixture.Online.ResolvedTracks, id => Assert.Equal(a.Id, id));
    }

    [Fact]
    public async Task CompletedOperationWithThrowingCancellationCallbackCannotStrandTheNextRepeat()
    {
        await using var fixture = new Fixture();
        var a = Track("A"); var b = Track("B");
        fixture.Queue.PlayMode = PlayMode.RepeatOne;
        var cancellationCalls = 0;
        fixture.Online.ResolveHook = (track, call, token) =>
        {
            token.Register(() =>
            {
                Interlocked.Increment(ref cancellationCalls);
                throw new ObjectDisposedException("completed provider process");
            });
            return Task.FromResult(Full(track, call));
        };
        await fixture.Coordinator.PlayAsync(a, [a, b]);
        for (var round = 1; round <= 5; round++)
        {
            fixture.Audio.NaturalComplete(fixture.Coordinator.CurrentSessionId);
            await UntilAsync(() => fixture.Audio.PlayCalls == round + 1 && fixture.Coordinator.Snapshot.State == PlaybackState.Playing);
        }
        Assert.Equal(5, cancellationCalls);
        Assert.Equal(6, fixture.Online.ResolveCalls);
        Assert.All(fixture.Online.ResolvedTracks, id => Assert.Equal(a.Id, id));
    }

    [Fact]
    public async Task RepeatResolutionFailureUsesOneRetryKeepsCurrentAndCanResumeIt()
    {
        await using var fixture = new Fixture();
        var a = Track("A"); var b = Track("B");
        fixture.Queue.PlayMode = PlayMode.RepeatOne;
        var first = await fixture.Coordinator.PlayAsync(a, [a, b]);
        fixture.Online.ResolveHook = (_, _, _) => throw new HttpRequestException("temporary gateway failure");
        fixture.Audio.Complete(first.SessionId);
        await UntilAsync(() => fixture.Coordinator.Snapshot.State == PlaybackState.Error);
        Assert.Equal(3, fixture.Online.ResolveCalls);
        Assert.Equal(1, fixture.Audio.PlayCalls);
        Assert.Equal(a.Id, fixture.Coordinator.Snapshot.Track!.Id);
        Assert.Equal(a.Id, fixture.Queue.Current!.Id);
        Assert.Equal(PlayMode.RepeatOne, fixture.Queue.PlayMode);
        Assert.Null(fixture.Coordinator.Snapshot.PendingTrack);
        Assert.All(fixture.Online.ResolvedTracks, id => Assert.Equal(a.Id, id));
        fixture.Audio.Complete(first.SessionId);
        Assert.Equal(3, fixture.Online.ResolveCalls);

        fixture.Online.ResolveHook = null;
        await fixture.Coordinator.ResumeAsync();
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
        Assert.Equal(a.Id, fixture.Coordinator.Snapshot.Track!.Id);
        Assert.Equal(PlayMode.RepeatOne, fixture.Queue.PlayMode);
    }

    [Fact]
    public async Task RepeatMissingUrlIsRetriedAndCannotSelectTheNextTrack()
    {
        await using var fixture = new Fixture();
        var a = Track("A"); var b = Track("B");
        fixture.Queue.PlayMode = PlayMode.RepeatOne;
        var first = await fixture.Coordinator.PlayAsync(a, [a, b]);
        fixture.Online.ResolveHook = (_, _, _) => Task.FromResult(new PlaybackResolution(MusicAvailability.Unavailable, null, "expired URL"));
        fixture.Audio.Complete(first.SessionId);
        await UntilAsync(() => fixture.Coordinator.Snapshot.State == PlaybackState.Error);
        Assert.Equal(3, fixture.Online.ResolveCalls);
        Assert.Equal(1, fixture.Audio.LoadAttempts);
        Assert.Equal(a.Id, fixture.Queue.Current!.Id);
        Assert.All(fixture.Online.ResolvedTracks, id => Assert.Equal(a.Id, id));
    }

    [Fact]
    public async Task RepeatLoadFailureUsesOneRetryKeepsErrorAndRejectsLateEof()
    {
        await using var fixture = new Fixture();
        var a = Track("A"); var b = Track("B");
        fixture.Queue.PlayMode = PlayMode.RepeatOne;
        var first = await fixture.Coordinator.PlayAsync(a, [a, b]);
        fixture.Audio.LoadHook = (_, _) => throw new InvalidOperationException("decode failed");
        fixture.Audio.Complete(first.SessionId);
        await UntilAsync(() => fixture.Coordinator.Snapshot.State == PlaybackState.Error && fixture.Coordinator.Snapshot.PendingTrack is null);
        Assert.Equal(3, fixture.Online.ResolveCalls);
        Assert.Equal(3, fixture.Audio.LoadAttempts);
        Assert.Equal(1, fixture.Audio.PlayCalls);
        Assert.Equal(a.Id, fixture.Coordinator.Snapshot.Track!.Id);
        Assert.Equal(a.Id, fixture.Queue.Current!.Id);
        Assert.Equal(PlayMode.RepeatOne, fixture.Queue.PlayMode);
        fixture.Audio.Complete(fixture.Coordinator.CurrentSessionId);
        Assert.Equal(3, fixture.Audio.LoadAttempts);
        Assert.Equal(PlaybackState.Error, fixture.Coordinator.Snapshot.State);
        Assert.All(fixture.Online.ResolvedTracks, id => Assert.Equal(a.Id, id));
    }

    [Fact]
    public async Task ResolveTimeoutAndLoadFailureShareTheSameRetryBudget()
    {
        await using var fixture = new Fixture();
        var a = Track("A"); var b = Track("B");
        fixture.Queue.PlayMode = PlayMode.RepeatOne;
        var first = await fixture.Coordinator.PlayAsync(a, [a, b]);
        fixture.Online.ResolveHook = (track, call, _) => call == 2
            ? throw new OperationCanceledException("provider timeout") : Task.FromResult(Full(track, call));
        fixture.Audio.LoadHook = (_, _) => throw new InvalidOperationException("second attempt decode failed");
        fixture.Audio.Complete(first.SessionId);
        await UntilAsync(() => fixture.Coordinator.Snapshot.State == PlaybackState.Error && fixture.Coordinator.Snapshot.PendingTrack is null);
        Assert.Equal(3, fixture.Online.ResolveCalls);
        Assert.Equal(2, fixture.Audio.LoadAttempts);
        Assert.Equal(1, fixture.Audio.PlayCalls);
        Assert.Equal(a.Id, fixture.Queue.Current!.Id);
    }

    [Fact]
    public async Task EachRepeatRoundGetsItsOwnSingleTransientRetry()
    {
        await using var fixture = new Fixture();
        var a = Track("A");
        fixture.Queue.PlayMode = PlayMode.RepeatOne;
        await fixture.Coordinator.PlayAsync(a);
        fixture.Online.ResolveHook = (track, call, _) => call % 2 == 0
            ? throw new HttpRequestException("temporary failure") : Task.FromResult(Full(track, call));
        for (var round = 1; round <= 5; round++)
        {
            fixture.Audio.Complete(fixture.Coordinator.CurrentSessionId);
            await UntilAsync(() => fixture.Audio.PlayCalls == round + 1 && fixture.Coordinator.Snapshot.State == PlaybackState.Playing);
            Assert.Equal(1 + round * 2, fixture.Online.ResolveCalls);
        }
        Assert.Equal(6, fixture.Audio.LoadAttempts);
    }

    [Fact]
    public async Task ManualNextAndPreviousStillNavigateInRepeatOne()
    {
        await using var fixture = new Fixture();
        var a = Track("A"); var b = Track("B");
        fixture.Queue.PlayMode = PlayMode.RepeatOne;
        await fixture.Coordinator.PlayAsync(a, [a, b]);
        await fixture.Coordinator.NextAsync();
        Assert.Equal(b.Id, fixture.Coordinator.Snapshot.Track!.Id);
        await fixture.Coordinator.PreviousAsync();
        Assert.Equal(a.Id, fixture.Coordinator.Snapshot.Track!.Id);
        Assert.Equal(PlayMode.RepeatOne, fixture.Queue.PlayMode);
    }

    [Fact]
    public async Task PreviewCompletionStopsWithoutRepeatingOrAdvancing()
    {
        await using var fixture = new Fixture();
        var a = Track("preview"); var b = Track("B");
        fixture.Queue.PlayMode = PlayMode.RepeatOne;
        fixture.Online.ResolveHook = (track, call, _) => Task.FromResult(new PlaybackResolution(MusicAvailability.Preview,
            Full(track, call).Source! with { IsPreview = true, Duration = TimeSpan.FromSeconds(30) }));
        var first = await fixture.Coordinator.PlayAsync(a, [a, b], preview: true);
        fixture.Audio.Complete(first.SessionId, preview: true);
        await UntilAsync(() => fixture.Coordinator.Snapshot.State == PlaybackState.Stopped && fixture.Audio.LoadedSource is null);
        Assert.Equal(1, fixture.Online.ResolveCalls);
        Assert.Equal(1, fixture.Audio.PlayCalls);
        Assert.Equal(a.Id, fixture.Queue.Current!.Id);
    }

    [Fact]
    public async Task PauseAndResumeAreIdempotentAndSeekingWhilePausedDoesNotReload()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Coordinator.PlayAsync(Track("A"));
        await fixture.Coordinator.PauseAsync();
        await fixture.Coordinator.PauseAsync();
        fixture.Audio.Complete(first.SessionId);
        await fixture.Coordinator.SeekAsync(TimeSpan.FromSeconds(42));
        Assert.Equal(PlaybackState.Paused, fixture.Coordinator.Snapshot.State);
        Assert.Equal(TimeSpan.FromSeconds(42), fixture.Coordinator.Snapshot.Position);
        Assert.Equal(1, fixture.Audio.PauseCalls);
        await fixture.Coordinator.ResumeAsync();
        await fixture.Coordinator.ResumeAsync();
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
        Assert.Equal(first.SessionId, fixture.Coordinator.CurrentSessionId);
        Assert.Equal(1, fixture.Online.ResolveCalls);
        Assert.Equal(1, fixture.Audio.LoadAttempts);
        Assert.Equal(2, fixture.Audio.PlayCalls);
    }

    [Fact]
    public async Task PauseDuringResolutionCancelsPendingSwitchAndCannotLaterPlayIt()
    {
        await using var fixture = new Fixture();
        var a = Track("A"); var b = Track("B");
        await fixture.Coordinator.PlayAsync(a, [a, b]);
        var resolution = Completion<PlaybackResolution>();
        fixture.Online.ResolveHook = (_, _, _) => resolution.Task;
        var switchTask = fixture.Coordinator.PlayAsync(b);
        await fixture.Coordinator.PauseAsync();
        await fixture.Coordinator.PauseAsync();
        resolution.SetResult(Full(b, 2));
        Assert.False((await switchTask).Started);
        Assert.Equal(a.Id, fixture.Coordinator.Snapshot.Track!.Id);
        Assert.Equal(PlaybackState.Stopped, fixture.Coordinator.Snapshot.State);
        Assert.Null(fixture.Coordinator.Snapshot.PendingTrack);
        Assert.Null(fixture.Audio.LoadedSource);
        Assert.Equal(1, fixture.Audio.LoadAttempts);
        Assert.Equal(1, fixture.Audio.PlayCalls);
    }

    [Fact]
    public async Task PauseDuringUncancellableLoadUnloadsItsLateResultBeforeAnyPlay()
    {
        await using var fixture = new Fixture();
        var entered = Completion<bool>(); var release = Completion<bool>();
        fixture.Audio.LoadHook = async (_, _) => { entered.TrySetResult(true); await release.Task; };
        var request = fixture.Coordinator.PlayAsync(Track("A"));
        await entered.Task;
        var pause = fixture.Coordinator.PauseAsync();
        release.SetResult(true);
        await pause;
        Assert.False((await request).Started);
        Assert.Equal(0, fixture.Audio.PlayCalls);
        Assert.Null(fixture.Audio.LoadedSource);
        Assert.Equal(PlaybackState.Stopped, fixture.Coordinator.Snapshot.State);
        fixture.Audio.LoadHook = null;
        await fixture.Coordinator.ResumeAsync();
        Assert.Equal(1, fixture.Audio.PlayCalls);
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
    }

    [Fact]
    public async Task LyricsRetryPublishesLoadingAndReadyWithoutChangingPausedAudioOrSeek()
    {
        await using var fixture = new Fixture();
        fixture.Online.LyricsHook = (_, _, _) => throw new HttpRequestException("lyrics offline");
        var first = await fixture.Coordinator.PlayAsync(Track("A"));
        await UntilAsync(() => fixture.LyricsEvents.Any(x => x.State == LyricsLoadingState.Error));
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
        Assert.Contains("lyrics offline", fixture.LyricsEvents.Last(x => x.State == LyricsLoadingState.Error).Message!);
        await fixture.Coordinator.PauseAsync();
        await fixture.Coordinator.SeekAsync(TimeSpan.FromSeconds(12));
        fixture.Online.LyricsHook = (_, _, _) => Task.FromResult<IReadOnlyList<LyricsLine>>([new(TimeSpan.Zero, "recovered")]);
        await fixture.Coordinator.RetryLyricsAsync();
        Assert.Equal(LyricsLoadingState.Ready, fixture.LyricsEvents.Last().State);
        Assert.Equal("recovered", fixture.LyricsEvents.Last().Lines[0].Text);
        Assert.Equal(2, fixture.LyricsEvents.Count(x => x.State == LyricsLoadingState.Loading));
        Assert.Equal(PlaybackState.Paused, fixture.Coordinator.Snapshot.State);
        Assert.Equal(TimeSpan.FromSeconds(12), fixture.Coordinator.Snapshot.Position);
        Assert.Equal(first.SessionId, fixture.Coordinator.CurrentSessionId);
        Assert.Null(fixture.Coordinator.LyricsMessage);
        Assert.Null(fixture.Coordinator.Snapshot.Message);
        Assert.Equal(1, fixture.Online.ResolveCalls);
        Assert.Equal(1, fixture.Audio.LoadAttempts);
        Assert.Equal(1, fixture.Audio.PlayCalls);
    }

    [Fact]
    public async Task LaterLyricsRetryCancelsAndIsolatesOlderRequestEvenWhenProviderIgnoresCancellation()
    {
        await using var fixture = new Fixture();
        await fixture.Coordinator.PlayAsync(Track("A"));
        await UntilAsync(() => fixture.LyricsEvents.Any(x => x.State == LyricsLoadingState.Empty));
        var older = Completion<IReadOnlyList<LyricsLine>>();
        CancellationToken olderToken = default;
        fixture.Online.LyricsHook = (_, _, token) => { olderToken = token; return older.Task; };
        var firstRetry = fixture.Coordinator.RetryLyricsAsync();
        fixture.Online.LyricsHook = (_, _, _) => Task.FromResult<IReadOnlyList<LyricsLine>>([new(TimeSpan.Zero, "latest")]);
        await fixture.Coordinator.RetryLyricsAsync();
        Assert.True(olderToken.IsCancellationRequested);
        older.SetResult([new(TimeSpan.Zero, "stale")]);
        await firstRetry;
        Assert.Equal("latest", fixture.LyricsEvents.Last().Lines[0].Text);
        Assert.DoesNotContain(fixture.LyricsEvents, x => x.Lines.Any(line => line.Text == "stale"));
        Assert.Equal(1, fixture.Audio.LoadAttempts);
        Assert.Equal(1, fixture.Audio.PlayCalls);
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
    }

    [Fact]
    public async Task LyricsRetryCannotDeliverIntoAnotherTrackSession()
    {
        await using var fixture = new Fixture();
        var a = Track("A"); var b = Track("B");
        await fixture.Coordinator.PlayAsync(a, [a, b]);
        await UntilAsync(() => fixture.LyricsEvents.Any(x => x.State == LyricsLoadingState.Empty));
        var older = Completion<IReadOnlyList<LyricsLine>>();
        fixture.Online.LyricsHook = (track, _, _) => track.Id == a.Id ? older.Task
            : Task.FromResult<IReadOnlyList<LyricsLine>>([new(TimeSpan.Zero, "B lyrics")]);
        var retry = fixture.Coordinator.RetryLyricsAsync();
        var second = await fixture.Coordinator.PlayAsync(b);
        older.SetResult([new(TimeSpan.Zero, "A stale retry")]);
        await retry;
        await UntilAsync(() => fixture.LyricsEvents.Any(x => x.State == LyricsLoadingState.Ready && x.TrackId == b.Id));
        Assert.Equal(second.SessionId, fixture.LyricsEvents.Last().SessionId);
        Assert.DoesNotContain(fixture.LyricsEvents, x => x.Lines.Any(line => line.Text == "A stale retry"));
        Assert.Equal(2, fixture.Audio.LoadAttempts);
    }

    [Fact]
    public async Task CancelledLyricsRetryDoesNotCancelPlayback()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Coordinator.PlayAsync(Track("A"));
        await UntilAsync(() => fixture.LyricsEvents.Any(x => x.State == LyricsLoadingState.Empty));
        var delayed = Completion<IReadOnlyList<LyricsLine>>();
        fixture.Online.LyricsHook = (_, _, _) => delayed.Task;
        using var cancellation = new CancellationTokenSource();
        var retry = fixture.Coordinator.RetryLyricsAsync(cancellation.Token);
        cancellation.Cancel();
        delayed.SetResult([new(TimeSpan.Zero, "cancelled")]);
        await retry;
        Assert.Equal(LyricsLoadingState.None, fixture.LyricsEvents.Last().State);
        Assert.Equal(first.SessionId, fixture.Coordinator.CurrentSessionId);
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
        Assert.Equal(1, fixture.Online.ResolveCalls);
        Assert.Equal(1, fixture.Audio.LoadAttempts);
        Assert.Equal(1, fixture.Audio.PlayCalls);
    }

    [Fact]
    public async Task ReorderingAndClearingPendingQueueKeepTheCurrentAudioSession()
    {
        await using var fixture = new Fixture();
        var a = Track("A"); var b = Track("B"); var c = Track("C");
        var first = await fixture.Coordinator.PlayAsync(b, [a, b, c]);
        fixture.Queue.Move(c.Id, 0);
        fixture.Queue.Move(b.Id, 0);
        fixture.Queue.ClearPending();
        Assert.Equal(b.Id, Assert.Single(fixture.Queue.Items).Id);
        Assert.Equal(b.Id, fixture.Queue.Current!.Id);
        Assert.Equal(first.SessionId, fixture.Coordinator.CurrentSessionId);
        Assert.Equal(PlaybackState.Playing, fixture.Coordinator.Snapshot.State);
        Assert.Equal(1, fixture.Audio.LoadAttempts);
        Assert.Equal(1, fixture.Audio.PlayCalls);
    }

    private static Track Track(string title) => new()
    {
        Id = Guid.NewGuid(), Title = title, SourceKind = TrackSourceKind.Online,
        ProviderId = "test", ProviderTrackId = title, Duration = TimeSpan.FromMinutes(3)
    };

    private static PlaybackResolution Full(Track track, int call) => new(MusicAvailability.Full,
        new AudioSource($"https://music.invalid/{track.ProviderTrackId}?resolve={call}", true, track.Duration));
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeAudio Audio { get; } = new();
        public FakeOnline Online { get; } = new();
        public PlaybackQueueService Queue { get; } = new();
        public PlaybackCoordinator Coordinator { get; }
        public ConcurrentQueue<PlaybackLyrics> LyricsEvents { get; } = new();
        public Fixture()
        {
            Coordinator = new(Audio, Queue, Online, new LrcParser(), new FakeCatalog());
            Coordinator.LyricsChanged += (_, value) => LyricsEvents.Enqueue(value);
        }
        public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
    }

    private sealed class FakeOnline : IOnlineMusicService
    {
        private int _resolveCalls;
        private int _lyricsCalls;
        public int ResolveCalls => Volatile.Read(ref _resolveCalls);
        public ConcurrentQueue<Guid> ResolvedTracks { get; } = new();
        public Func<Track, int, CancellationToken, Task<PlaybackResolution>>? ResolveHook { get; set; }
        public Func<Track, int, CancellationToken, Task<IReadOnlyList<LyricsLine>>>? LyricsHook { get; set; }
        public IReadOnlyList<MusicProviderInfo> Providers => [];
        public Task<MusicSearchPage> SearchAsync(MusicSearchRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<PlaybackResolution> ResolveAsync(Track track, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _resolveCalls);
            ResolvedTracks.Enqueue(track.Id);
            return ResolveHook?.Invoke(track, call, cancellationToken) ?? Task.FromResult(Full(track, call));
        }
        public Task<IReadOnlyList<LyricsLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
            => LyricsHook?.Invoke(track, Interlocked.Increment(ref _lyricsCalls), cancellationToken)
                ?? Task.FromResult<IReadOnlyList<LyricsLine>>([]);
    }

    private sealed class FakeCatalog : ITrackCatalog
    {
        public Task<IReadOnlyList<Track>> GetTracksByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Track>>([]);
        public Task<Track> EnsureOnlineTrackAsync(Track track, CancellationToken cancellationToken = default) => Task.FromResult(track);
        public Task<IReadOnlyList<Track>> EnsureOnlineTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Track>>(tracks.ToArray());
    }

    private sealed class FakeAudio : ISessionAudioPlayerService
    {
        private int _loadAttempts;
        private int _playCalls;
        private int _pauseCalls;
        public int LoadAttempts => Volatile.Read(ref _loadAttempts);
        public int PlayCalls => Volatile.Read(ref _playCalls);
        public int PauseCalls => Volatile.Read(ref _pauseCalls);
        public ConcurrentQueue<AudioSource> AttemptedSources { get; } = new();
        public Func<AudioSource, CancellationToken, Task>? LoadHook { get; set; }
        public AudioSource? LoadedSource { get; private set; }
        public PlaybackState State { get; private set; }
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration { get; private set; }
        public float Volume { get; set; } = 1;
        public bool IsMuted { get; set; }
        public long SessionId { get; private set; }
        public bool CanSeek => LoadedSource?.CanSeek == true;
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
            Interlocked.Increment(ref _loadAttempts);
            AttemptedSources.Enqueue(source);
            if (LoadHook is not null) await LoadHook(source, cancellationToken);
            LoadedSource = source;
            SessionId = source.SessionId;
            Position = startPosition ?? TimeSpan.Zero;
            Duration = source.Duration ?? TimeSpan.FromMinutes(3);
            StateEvent(PlaybackState.Loading);
        }
        public Task PlayAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _playCalls);
            StateEvent(PlaybackState.Playing);
            return Task.CompletedTask;
        }
        public Task PauseAsync()
        { Interlocked.Increment(ref _pauseCalls); StateEvent(PlaybackState.Paused); return Task.CompletedTask; }
        public Task StopAsync() { Position = TimeSpan.Zero; StateEvent(PlaybackState.Stopped); return Task.CompletedTask; }
        public Task UnloadAsync()
        { LoadedSource = null; Position = TimeSpan.Zero; StateEvent(PlaybackState.Idle); return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        { Position = position; PositionEvent(SessionId, position); return Task.CompletedTask; }
        private void StateEvent(PlaybackState state)
        { State = state; StateChanged?.Invoke(this, state); SessionStateChanged?.Invoke(this, new(SessionId, state)); }
        public void PositionEvent(long session, TimeSpan position)
        { PositionChanged?.Invoke(this, position); SessionPositionChanged?.Invoke(this, new(session, position)); }
        public void Complete(long session, bool preview = false)
        { PlaybackCompleted?.Invoke(this, EventArgs.Empty); SessionPlaybackCompleted?.Invoke(this, new(session, preview)); }
        public void NaturalComplete(long session)
        {
            // FFmpeg publishes Stopped and its final position before the EOF event.
            StateEvent(PlaybackState.Stopped);
            Position = Duration;
            PositionEvent(session, Duration);
            Complete(session);
        }
        public void Fail(long session)
        { var error = new InvalidOperationException("expired source"); PlaybackFailed?.Invoke(this, error); SessionPlaybackFailed?.Invoke(this, new(session, error)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
