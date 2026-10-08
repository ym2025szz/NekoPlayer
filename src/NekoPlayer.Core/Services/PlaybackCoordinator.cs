using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Core.Services;

/// <summary>Owns playback intent, queue selection and the lifetime of transient audio sources.</summary>
public sealed class PlaybackCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private readonly IAudioPlayerService _audio;
    private readonly ISessionAudioPlayerService? _sessionAudio;
    private readonly IPlaybackQueueService _queue;
    private readonly IOnlineMusicService _online;
    private readonly ILyricsService _lyrics;
    private readonly ITrackCatalog _catalog;
    private CancellationTokenSource? _requestCancellation;
    private CancellationTokenSource? _lyricsCancellation;
    private long _lyricsGeneration;
    private bool _lyricsLoading;
    private long _generation;
    private long _nextSessionId;
    private long _activeSessionId;
    private long _activeGeneration;
    private long _completedSessionId;
    private bool _suppressActiveEvents;
    private bool _loading;
    private bool _retryUsed;
    private bool _wantsPlaying;
    private bool _disposed;
    private Exception? _loadingError;
    private AudioSessionCompletion? _deferredCompletion;
    private AudioSource? _source;
    private TimeSpan? _desiredPosition;
    private string? _lyricsMessage;
    private PlaybackSnapshot _snapshot = new(0, null, null, PlaybackState.Idle,
        TimeSpan.Zero, TimeSpan.Zero, false);

    public PlaybackCoordinator(IAudioPlayerService audio, IPlaybackQueueService queue,
        IOnlineMusicService online, ILyricsService lyrics, ITrackCatalog catalog)
    {
        _audio = audio;
        _sessionAudio = audio as ISessionAudioPlayerService;
        _queue = queue;
        _online = online;
        _lyrics = lyrics;
        _catalog = catalog;
        if (_sessionAudio is not null)
        {
            _sessionAudio.SessionStateChanged += OnState;
            _sessionAudio.SessionPositionChanged += OnPosition;
            _sessionAudio.SessionPlaybackFailed += OnFailure;
            _sessionAudio.SessionPlaybackCompleted += OnCompletion;
        }
        else
        {
            _audio.StateChanged += OnLegacyState;
            _audio.PositionChanged += OnLegacyPosition;
            _audio.PlaybackFailed += OnLegacyFailure;
            _audio.PlaybackCompleted += OnLegacyCompletion;
        }
    }

    public PlaybackSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public long CurrentSessionId { get { lock (_sync) return _snapshot.SessionId; } }
    public long CurrentIntentId { get { lock (_sync) return _generation; } }
    public string? LyricsMessage { get { lock (_sync) return _lyricsMessage; } }
    public event EventHandler<PlaybackSnapshot>? SnapshotChanged;
    public event EventHandler<PlaybackLyrics>? LyricsChanged;
    public event EventHandler<PlaybackRequestResult>? PlaybackStarted;

    public async Task<PlaybackRequestResult> PlayAsync(Track track, IReadOnlyList<Track>? context = null,
        bool preview = false, TimeSpan? startPosition = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        // Freeze the caller's source now: changing a search tab later cannot change this queue.
        Track[] source;
        lock (_sync)
        {
            ThrowIfDisposed();
            source = context?.ToArray() ?? (_queue.Items.Any(x => x.Id == track.Id) ? _queue.Items.ToArray() : [track]);
        }
        if (!source.Any(x => x.Id == track.Id)) source = [.. source, track];
        source = source.DistinctBy(x => x.Id).ToArray();
        var intent = BeginIntent(track, cancellationToken);
        var selected = track;
        try
        {
            var canonical = await CanonicalizeAsync(source, intent.Token).ConfigureAwait(false);
            if (!IsCurrent(intent)) return CancelRequest(intent, track);
            selected = canonical.FirstOrDefault(x => SameIdentity(x, track)) ?? track;
            if (!canonical.Any(x => x.Id == selected.Id)) canonical = [.. canonical, selected];
            var resolution = await ResolveWithRetryAsync(intent, selected).ConfigureAwait(false);
            if (!IsCurrent(intent)) return CancelRequest(intent, selected);
            if (!ApplyResolution(intent, selected, resolution)) return CancelRequest(intent, selected);
            var rejection = ResolutionError(resolution, preview);
            if (rejection is not null) return FailBeforeSwitch(intent, selected, rejection);
            return await StartAsync(intent, selected, NormalizeSource(resolution), canonical, preview,
                startPosition, publishStarted: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (intent.Token.IsCancellationRequested) { return CancelRequest(intent, selected); }
        catch (Exception ex) { return FailBeforeSwitch(intent, selected, ex.Message); }
    }

    public async Task ToggleAsync()
    {
        bool resume;
        lock (_sync)
        {
            ThrowIfDisposed();
            resume = !_wantsPlaying || (_snapshot.PendingTrack is null && !_loading &&
                _snapshot.State is PlaybackState.Stopped or PlaybackState.Error or PlaybackState.Idle);
        }
        if (resume) await ResumeAsync().ConfigureAwait(false);
        else await PauseAsync().ConfigureAwait(false);
    }

    public async Task PauseAsync()
    {
        long session;
        Intent intent;
        bool unload;
        lock (_sync)
        {
            ThrowIfDisposed();
            unload = _snapshot.PendingTrack is not null || _loading || (_suppressActiveEvents && _activeSessionId != 0 && _wantsPlaying);
            if (!_wantsPlaying && !unload) return;
            if (unload)
            {
                var selected = _snapshot.Track ?? _snapshot.PendingTrack;
                intent = BeginIntent(null, CancellationToken.None);
                _activeSessionId = 0;
                _suppressActiveEvents = false;
                _loading = false;
                _source = null;
                _desiredPosition = null;
                _wantsPlaying = false;
                Publish(_snapshot with { Track = selected, PendingTrack = null, State = PlaybackState.Stopped,
                    CanSeek = false, Message = null });
                session = 0;
            }
            else
            {
                _wantsPlaying = false;
                if (_activeSessionId == 0 || _snapshot.State == PlaybackState.Paused) return;
                session = _activeSessionId;
                intent = CurrentIntent();
            }
        }
        if (unload)
        {
            await StopAudioAsync(intent, unload: true).ConfigureAwait(false);
            return;
        }
        await SetPlayingAsync(intent, session, playing: false).ConfigureAwait(false);
    }

    public async Task ResumeAsync()
    {
        long session;
        Intent intent;
        Track? restart;
        bool preview;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_snapshot.PendingTrack is not null || _loading ||
                (_wantsPlaying && _snapshot.State is PlaybackState.Playing or PlaybackState.Buffering or PlaybackState.Seeking)) return;
            restart = _snapshot.State is PlaybackState.Stopped or PlaybackState.Error or PlaybackState.Idle ||
                _activeSessionId == 0 ? _snapshot.Track : null;
            preview = _snapshot.IsPreview;
            session = _activeSessionId;
            intent = CurrentIntent();
            if (restart is null)
            {
                if (session == 0 || _wantsPlaying) return;
                _wantsPlaying = true;
            }
        }
        if (restart is not null)
        {
            await PlayAsync(restart, preview: preview).ConfigureAwait(false);
            return;
        }
        await SetPlayingAsync(intent, session, playing: true).ConfigureAwait(false);
    }

    private async Task SetPlayingAsync(Intent intent, long session, bool playing)
    {
        try
        {
            await _audioGate.WaitAsync(intent.Token).ConfigureAwait(false);
            try
            {
                lock (_sync) if (!IsActiveUnsafe(intent, session) || _wantsPlaying != playing) return;
                if (!playing) await _audio.PauseAsync().ConfigureAwait(false);
                else await _audio.PlayAsync(intent.Token).ConfigureAwait(false);
                lock (_sync)
                    if (IsActiveUnsafe(intent, session) && _wantsPlaying == playing)
                        Publish(_snapshot with { State = playing ? PlaybackState.Playing : PlaybackState.Paused });
            }
            finally { _audioGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetActiveError(intent, session, ex.Message); }
    }

    public Task RetryLyricsAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_snapshot.Track is not { } track || _snapshot.PendingTrack is not null || _activeSessionId == 0) return Task.CompletedTask;
            var intent = CurrentIntent();
            if (!IsCurrentUnsafe(intent)) return Task.CompletedTask;
            var request = BeginLyricsRequestUnsafe(intent, _activeSessionId, track, cancellationToken);
            return LoadLyricsAsync(intent, _activeSessionId, track, request);
        }
    }

    public Task NextAsync() => NavigateAsync(previous: false, completed: false);
    public Task PreviousAsync() => NavigateAsync(previous: true, completed: false);

    public async Task StopAsync()
    {
        bool lyricsWereLoading;
        lock (_sync) lyricsWereLoading = _lyricsLoading;
        var intent = BeginIntent(null, CancellationToken.None);
        lock (_sync)
        {
            if (!IsCurrentUnsafe(intent)) return;
            _activeSessionId = 0;
            _loading = false;
            _source = null;
            _desiredPosition = null;
            _wantsPlaying = false;
            Publish(_snapshot with { SessionId = ++_nextSessionId, PendingTrack = null,
                State = PlaybackState.Stopped, Position = TimeSpan.Zero, CanSeek = false, Message = null });
            if (lyricsWereLoading && _snapshot.Track is { } track)
                LyricsChanged?.Invoke(this, new(_snapshot.SessionId, track.Id, [], LyricsLoadingState.None));
        }
        await StopAudioAsync(intent, unload: true).ConfigureAwait(false);
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        Intent intent;
        long session;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_snapshot.CanSeek || _snapshot.PendingTrack is not null || _activeSessionId == 0) return;
            intent = CurrentIntent();
            session = _activeSessionId;
            position = ClampPosition(position, _snapshot.Duration);
            _desiredPosition = position;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(intent.Token, cancellationToken);
        try
        {
            await _audioGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (!IsActive(intent, session)) return;
                await _audio.SeekAsync(position, linked.Token).ConfigureAwait(false);
                lock (_sync)
                    if (IsActiveUnsafe(intent, session))
                    {
                        _desiredPosition = null;
                        Publish(_snapshot with { Position = position });
                    }
            }
            finally { _audioGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetActiveError(intent, session, ex.Message); }
        finally
        {
            lock (_sync)
                if (IsActiveUnsafe(intent, session) && _desiredPosition == position) _desiredPosition = null;
        }
    }

    public async Task AddToQueueAsync(Track track)
    {
        var canonical = track.IsOnline ? await _catalog.EnsureOnlineTrackAsync(track).ConfigureAwait(false) : track;
        lock (_sync) { ThrowIfDisposed(); _queue.Add(canonical); }
    }

    public async Task PlayNextAsync(Track track)
    {
        var canonical = track.IsOnline ? await _catalog.EnsureOnlineTrackAsync(track).ConfigureAwait(false) : track;
        lock (_sync) { ThrowIfDisposed(); _queue.PlayNext(canonical); }
    }

    public async Task RemoveQueueItemAsync(Guid trackId)
    {
        Intent intent;
        lock (_sync)
        {
            ThrowIfDisposed();
            var active = _snapshot.Track?.Id == trackId || _snapshot.PendingTrack?.Id == trackId;
            _queue.Remove(trackId);
            if (!active) return;
            intent = BeginIntent(null, CancellationToken.None);
            _queue.ClearCurrent();
            _activeSessionId = 0;
            _loading = false;
            _source = null;
            _desiredPosition = null;
            _wantsPlaying = false;
            _lyricsMessage = null;
            Publish(new(++_nextSessionId, null, null, PlaybackState.Stopped, TimeSpan.Zero, TimeSpan.Zero, false));
            LyricsChanged?.Invoke(this, new(_snapshot.SessionId, Guid.Empty, []));
        }
        await StopAudioAsync(intent, unload: true).ConfigureAwait(false);
    }

    private async Task NavigateAsync(bool previous, bool completed, long? expectedSession = null)
    {
        Track[] candidates;
        Intent? intent = null;
        bool restart;
        bool repeatCurrent;
        string? failureMessage = null;
        lock (_sync)
        {
            if (_disposed || (expectedSession is { } expected && !AcceptSession(expected))) return;
            restart = previous && _snapshot.Position > TimeSpan.FromSeconds(3) && _snapshot.CanSeek;
            repeatCurrent = !previous && completed && _queue.PlayMode == PlayMode.RepeatOne;
            if (restart) candidates = [];
            else candidates = NavigationCandidates(previous, completed);
            if (!restart) intent = BeginIntent(null, CancellationToken.None);
        }
        if (restart)
        { await SeekAsync(TimeSpan.Zero).ConfigureAwait(false); return; }
        if (intent is null) return;
        foreach (var candidate in candidates)
        {
            if (!IsCurrent(intent)) return;
            if (!repeatCurrent && candidate.IsOnline && candidate.Availability is MusicAvailability.Preview or MusicAvailability.Unavailable) continue;
            lock (_sync) if (IsCurrentUnsafe(intent))
            {
                _suppressActiveEvents = true;
                _retryUsed = false;
                Publish(_snapshot with { PendingTrack = candidate, CanSeek = false });
            }
            try
            {
                var resolution = await ResolveWithRetryAsync(intent, candidate).ConfigureAwait(false);
                if (!IsCurrent(intent)) return;
                if (!ApplyResolution(intent, candidate, resolution)) return;
                if (ResolutionError(resolution, preview: false) is { } rejection)
                { failureMessage = rejection; continue; }
                var result = await StartAsync(intent, candidate, NormalizeSource(resolution), null, false, null,
                    publishStarted: true).ConfigureAwait(false);
                if (result.Started || !IsCurrent(intent)) return;
                failureMessage = result.Message;
            }
            catch (OperationCanceledException) when (intent.Token.IsCancellationRequested) { return; }
            catch (Exception ex) { failureMessage = ex.Message; }
        }
        if (!IsCurrent(intent)) return;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(intent)) return;
            _activeSessionId = 0;
            _loading = false;
            _source = null;
            _desiredPosition = null;
            _wantsPlaying = false;
            CancelLyricsUnsafe(clearLoading: true);
            Publish(_snapshot with { PendingTrack = null,
                State = repeatCurrent && candidates.Length > 0 ? PlaybackState.Error : PlaybackState.Stopped, CanSeek = false,
                Message = repeatCurrent && candidates.Length > 0 ? failureMessage ?? "单曲循环重播失败，请重试。"
                    : candidates.Length == 0 ? null : "队列中没有可完整播放的歌曲。" });
        }
        await StopAudioAsync(intent, unload: true).ConfigureAwait(false);
    }

    private Track[] NavigationCandidates(bool previous, bool completed)
    {
        var items = _queue.Items.ToArray();
        if (items.Length == 0) return [];
        var currentId = _snapshot.Track?.Id ?? _queue.Current?.Id;
        var index = Array.FindIndex(items, x => x.Id == currentId);
        var mode = _queue.PlayMode;
        if (!previous && completed && mode == PlayMode.RepeatOne && index >= 0)
            return [items[index]];
        if (!previous && mode == PlayMode.Shuffle)
        {
            var remaining = items.Where((_, i) => i != index).ToArray();
            Random.Shared.Shuffle(remaining);
            return index < 0 ? remaining : [.. remaining, items[index]];
        }
        var result = new List<Track>(items.Length);
        for (var step = 1; step <= items.Length; step++)
        {
            var candidateIndex = previous ? (index < 0 ? items.Length : index) - step : index + step;
            if (candidateIndex < 0 || candidateIndex >= items.Length)
            {
                if (mode == PlayMode.Sequential || (previous && mode != PlayMode.RepeatAll)) break;
                candidateIndex = (candidateIndex % items.Length + items.Length) % items.Length;
            }
            result.Add(items[candidateIndex]);
        }
        return result.ToArray();
    }

    private async Task<PlaybackRequestResult> StartAsync(Intent intent, Track track, AudioSource source,
        Track[]? context, bool preview, TimeSpan? position, bool publishStarted)
    {
        _ = preview; // Permission was checked against the provider's resolution before loading.
        var outcome = await LoadAttemptAsync(intent, track, source, context, position).ConfigureAwait(false);
        if (!outcome.Started && IsCurrent(intent) && source.IsRemote && TryUseRetry(intent))
        {
            try
            {
                var resolution = await ResolveWithRetryAsync(intent, track).ConfigureAwait(false);
                if (!IsCurrent(intent)) return CancelRequest(intent, track);
                if (!ApplyResolution(intent, track, resolution)) return CancelRequest(intent, track);
                var rejection = ResolutionError(resolution, source.IsPreview);
                if (rejection is not null) return FailAfterSwitch(intent, track, rejection);
                outcome = await LoadAttemptAsync(intent, track, NormalizeSource(resolution), null, position).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (intent.Token.IsCancellationRequested) { return CancelRequest(intent, track); }
            catch (Exception ex) { return FailAfterSwitch(intent, track, ex.Message); }
        }
        if (outcome.Started && publishStarted)
            lock (_sync) if (IsCurrentUnsafe(intent) && outcome.SessionId == _activeSessionId)
                PlaybackStarted?.Invoke(this, outcome);
        if (outcome.Started) DeliverDeferredCompletion(outcome.SessionId);
        return outcome;
    }

    private async Task<PlaybackRequestResult> LoadAttemptAsync(Intent intent, Track track, AudioSource source,
        Track[]? context, TimeSpan? position)
    {
        long session = 0;
        LyricsRequest? lyricsRequest = null;
        try
        {
            await _audioGate.WaitAsync(intent.Token).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (!IsCurrentUnsafe(intent)) return CancelRequest(intent, track);
                    // Only commit selection after resolution succeeded. The existing queue wins
                    // when it already contains the target, preserving later queue edits.
                    if (_queue.SetCurrent(track.Id) is null)
                    {
                        if (context is null) return FailBeforeSwitch(intent, track, "歌曲已从播放队列移除。");
                        _queue.Replace(context, track.Id);
                    }
                    session = ++_nextSessionId;
                    _activeSessionId = session;
                    _activeGeneration = intent.Generation;
                    _completedSessionId = 0;
                    _suppressActiveEvents = false;
                    _loading = true;
                    _loadingError = null;
                    _deferredCompletion = null;
                    _source = source with { SessionId = session };
                    _desiredPosition = null;
                    Publish(new(session, track, null, PlaybackState.Loading, position ?? TimeSpan.Zero,
                        source.Duration ?? (source.IsPreview ? TimeSpan.Zero : track.Duration), source.CanSeek, source.IsPreview));
                    lyricsRequest = BeginLyricsRequestUnsafe(intent, session, track, CancellationToken.None);
                }
                _ = Task.Run(() => LoadLyricsAsync(intent, session, track, lyricsRequest));
                if (_sessionAudio is not null)
                    await _sessionAudio.LoadAsync(source with { SessionId = session }, position, intent.Token).ConfigureAwait(false);
                else
                {
                    if (source.IsRemote) throw new NotSupportedException("当前音频服务不支持在线播放。");
                    await _audio.LoadAsync(source.Input, position, intent.Token).ConfigureAwait(false);
                }
                if (!IsActive(intent, session)) return CancelRequest(intent, track);
                bool play;
                lock (_sync) play = _wantsPlaying;
                if (play) await _audio.PlayAsync(intent.Token).ConfigureAwait(false);
                else await _audio.PauseAsync().ConfigureAwait(false);
                lock (_sync)
                {
                    if (!IsActiveUnsafe(intent, session)) return CancelRequest(intent, track);
                    if (_loadingError is not null) throw _loadingError;
                    _loading = false;
                    var duration = _audio.Duration > TimeSpan.Zero ? _audio.Duration : _snapshot.Duration;
                    Publish(_snapshot with { State = play ? PlaybackState.Playing : PlaybackState.Paused,
                        Duration = duration, CanSeek = source.CanSeek && (_sessionAudio?.CanSeek ?? true), Message = _lyricsMessage });
                    return new(true, track, session);
                }
            }
            finally { _audioGate.Release(); }
        }
        catch (OperationCanceledException) when (intent.Token.IsCancellationRequested) { return CancelRequest(intent, track); }
        catch (Exception ex) { return FailAfterSwitch(intent, track, ex.Message); }
        finally { lock (_sync) if (session != 0 && session == _activeSessionId) _loading = false; }
    }

    private async Task<Track[]> CanonicalizeAsync(Track[] tracks, CancellationToken token)
    {
        var online = tracks.Where(x => x.IsOnline).ToArray();
        if (online.Length == 0) return tracks;
        var saved = await _catalog.EnsureOnlineTracksAsync(online, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return tracks.Select(x => x.IsOnline ? saved.FirstOrDefault(y => SameIdentity(x, y))
                ?? throw new InvalidOperationException("在线歌曲保存失败。") : x)
            .DistinctBy(x => x.Id).ToArray();
    }

    private Task<PlaybackResolution> ResolveAsync(Track track, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (track.IsOnline) return _online.ResolveAsync(track, token);
        if (!track.FileExists) return Task.FromResult(new PlaybackResolution(MusicAvailability.Unavailable, null, "本地文件不存在。"));
        return Task.FromResult(new PlaybackResolution(MusicAvailability.Full,
            new AudioSource(track.FilePath, Duration: track.Duration)));
    }

    private async Task<PlaybackResolution> ResolveWithRetryAsync(Intent intent, Track track)
    {
        while (true)
        {
            try
            {
                var resolution = await ResolveAsync(track, intent.Token).ConfigureAwait(false);
                intent.Token.ThrowIfCancellationRequested();
                // A missing/expired URL may recover on the next resolution. Explicit preview
                // permission is not a transient error, and all failures share one retry budget.
                if (track.IsOnline && resolution.Availability != MusicAvailability.Preview &&
                    (resolution.Source is null || resolution.Availability is MusicAvailability.Unknown or MusicAvailability.Unavailable) &&
                    TryUseRetry(intent)) continue;
                return resolution;
            }
            catch (Exception) when (track.IsOnline && !intent.Token.IsCancellationRequested && TryUseRetry(intent)) { }
        }
    }

    private static bool SameIdentity(Track a, Track b) => a.Id == b.Id || (a.IsOnline && b.IsOnline &&
        !string.IsNullOrWhiteSpace(a.ProviderId) && !string.IsNullOrWhiteSpace(a.ProviderTrackId) &&
        string.Equals(a.ProviderId, b.ProviderId, StringComparison.OrdinalIgnoreCase) && a.ProviderTrackId == b.ProviderTrackId);

    private static string? ResolutionError(PlaybackResolution resolution, bool preview)
    {
        if (resolution.Source is null || resolution.Availability is MusicAvailability.Unavailable or MusicAvailability.Unknown)
            return resolution.Reason ?? "当前歌曲无法播放。";
        if ((resolution.Availability == MusicAvailability.Preview || resolution.Source.IsPreview) && !preview)
            return resolution.Reason ?? "当前歌曲仅支持试听，请明确选择试听。";
        return null;
    }

    private bool ApplyResolution(Intent intent, Track track, PlaybackResolution resolution)
    {
        lock (_sync)
        {
            if (!IsCurrentUnsafe(intent)) return false;
            if (track.IsOnline)
            {
                track.Availability = resolution.Source?.IsPreview == true ? MusicAvailability.Preview : resolution.Availability;
                track.RestrictionReason = resolution.Reason;
            }
            return true;
        }
    }

    private static AudioSource NormalizeSource(PlaybackResolution resolution) => resolution.Source! with
    { IsPreview = resolution.Source!.IsPreview || resolution.Availability == MusicAvailability.Preview };

    private LyricsRequest BeginLyricsRequestUnsafe(Intent intent, long session, Track track, CancellationToken external)
    {
        CancelLyricsUnsafe();
        _lyricsCancellation = CancellationTokenSource.CreateLinkedTokenSource(intent.Token, external);
        var request = new LyricsRequest(_lyricsGeneration, _lyricsCancellation.Token);
        var oldMessage = _lyricsMessage;
        _lyricsMessage = null;
        _lyricsLoading = true;
        if (oldMessage is not null && _snapshot.Message == oldMessage) Publish(_snapshot with { Message = null });
        LyricsChanged?.Invoke(this, new(session, track.Id, [], LyricsLoadingState.Loading));
        return request;
    }

    private void CancelLyricsUnsafe(bool clearLoading = false)
    {
        _lyricsGeneration++;
        var wasLoading = _lyricsLoading;
        _lyricsLoading = false;
        CancelAndDispose(_lyricsCancellation);
        _lyricsCancellation = null;
        if (clearLoading && wasLoading && !_disposed && _snapshot.Track is { } track)
            LyricsChanged?.Invoke(this, new(_snapshot.SessionId, track.Id, [], LyricsLoadingState.None));
    }

    private bool IsLyricsCurrentUnsafe(Intent intent, long session, LyricsRequest request) =>
        IsCurrentUnsafe(intent) && session == _activeSessionId && request.Generation == _lyricsGeneration;

    private async Task LoadLyricsAsync(Intent intent, long session, Track track, LyricsRequest request)
    {
        try
        {
            request.Token.ThrowIfCancellationRequested();
            var lines = track.IsOnline ? await _online.GetLyricsAsync(track, request.Token).ConfigureAwait(false)
                : await _lyrics.LoadForTrackAsync(track, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            lock (_sync)
                if (IsLyricsCurrentUnsafe(intent, session, request))
                {
                    _lyricsLoading = false;
                    var visibleLines = PreviewLyrics(lines, _source);
                    LyricsChanged?.Invoke(this, new(session, track.Id, visibleLines,
                        visibleLines.Count == 0 ? LyricsLoadingState.Empty : LyricsLoadingState.Ready,
                        visibleLines.Count == 0 ? "暂无歌词。" : null));
                }
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            lock (_sync) if (IsLyricsCurrentUnsafe(intent, session, request))
            {
                _lyricsLoading = false;
                LyricsChanged?.Invoke(this, new(session, track.Id, [], LyricsLoadingState.None));
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (!IsLyricsCurrentUnsafe(intent, session, request) || request.Token.IsCancellationRequested) return;
                _lyricsLoading = false;
                _lyricsMessage = "歌词获取失败：" + ex.Message;
                Publish(_snapshot with { Message = _snapshot.Message ?? _lyricsMessage });
                LyricsChanged?.Invoke(this, new(session, track.Id, [], LyricsLoadingState.Error, _lyricsMessage));
            }
        }
    }

    private static IReadOnlyList<LyricsLine> PreviewLyrics(IReadOnlyList<LyricsLine> lines, AudioSource? source)
    {
        if (source?.IsPreview != true || source.PreviewStart is not { } start) return lines;
        var ordered = lines.OrderBy(x => x.Timestamp).ToArray();
        var previous = ordered.LastOrDefault(x => x.Timestamp < start);
        var end = source.Duration is { } duration && duration > TimeSpan.Zero ? start + duration : (TimeSpan?)null;
        var result = new List<LyricsLine>();
        if (previous is not null) result.Add(new(TimeSpan.Zero, previous.Text));
        result.AddRange(ordered.Where(x => x.Timestamp >= start && (end is null || x.Timestamp < end))
            .Select(x => new LyricsLine(x.Timestamp - start, x.Text)));
        return result;
    }

    private Intent BeginIntent(Track? pending, CancellationToken external)
    {
        CancellationTokenSource? previous;
        Intent intent;
        lock (_sync)
        {
            ThrowIfDisposed();
            previous = _requestCancellation;
            CancelLyricsUnsafe(clearLoading: true);
            _requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(external);
            intent = new(++_generation, _requestCancellation.Token);
            _suppressActiveEvents = true;
            _retryUsed = false;
            _wantsPlaying = true;
            Publish(_snapshot with { PendingTrack = pending, CanSeek = pending is null ? _snapshot.CanSeek : false, Message = null });
        }
        CancelAndDispose(previous);
        return intent;
    }

    private PlaybackRequestResult FailBeforeSwitch(Intent intent, Track track, string message)
    {
        lock (_sync)
        {
            if (!IsCurrentUnsafe(intent)) return Canceled(track);
            if (_activeGeneration == intent.Generation && _snapshot.Track?.Id == track.Id)
                return FailAfterSwitch(intent, track, message);
            _suppressActiveEvents = false;
            _activeGeneration = intent.Generation;
            _wantsPlaying = _snapshot.State is PlaybackState.Playing or PlaybackState.Buffering or PlaybackState.Seeking;
            Publish(_snapshot.Track is null
                ? new(_snapshot.SessionId, track, null, PlaybackState.Error, TimeSpan.Zero, track.Duration, false, Message: message)
                : _snapshot with { PendingTrack = null, CanSeek = _source?.CanSeek ?? false, Message = message });
            return new(false, track, _snapshot.SessionId, message);
        }
    }

    private PlaybackRequestResult FailAfterSwitch(Intent intent, Track track, string message)
    {
        lock (_sync)
        {
            if (!IsCurrentUnsafe(intent)) return Canceled(track);
            _suppressActiveEvents = true;
            Publish(_snapshot with { Track = track, PendingTrack = null, State = PlaybackState.Error,
                CanSeek = false, Message = message });
            return new(false, track, _snapshot.SessionId, message);
        }
    }

    private PlaybackRequestResult CancelRequest(Intent intent, Track track)
    {
        Intent? cleanup = null;
        PlaybackRequestResult result;
        lock (_sync)
        {
            if (!_disposed && intent.Generation == _generation)
            {
                var switched = _activeGeneration == intent.Generation;
                _requestCancellation?.Dispose();
                _requestCancellation = new CancellationTokenSource();
                _suppressActiveEvents = false;
                _activeGeneration = _generation;
                if (switched)
                {
                    _activeSessionId = 0;
                    CancelLyricsUnsafe(clearLoading: true);
                    _loading = false;
                    _source = null;
                    _desiredPosition = null;
                    _wantsPlaying = false;
                    cleanup = CurrentIntent();
                }
                Publish(_snapshot with { PendingTrack = null,
                    State = switched ? PlaybackState.Stopped : _snapshot.State,
                    CanSeek = switched ? false : _source?.CanSeek ?? false });
            }
            result = Canceled(track);
        }
        if (cleanup is not null) _ = StopAudioAsync(cleanup, unload: true);
        return result;
    }

    private PlaybackRequestResult Canceled(Track track) => new(false, track, CurrentSessionId, "播放请求已取消。");
    private Intent CurrentIntent() => new(_generation, _requestCancellation?.Token ?? CancellationToken.None);
    private bool IsCurrent(Intent intent) { lock (_sync) return IsCurrentUnsafe(intent); }
    private bool IsCurrentUnsafe(Intent intent) => !_disposed && intent.Generation == _generation && !intent.Token.IsCancellationRequested;
    private bool IsActive(Intent intent, long session) { lock (_sync) return IsActiveUnsafe(intent, session); }
    private bool IsActiveUnsafe(Intent intent, long session) => IsCurrentUnsafe(intent) && session == _activeSessionId && !_suppressActiveEvents;
    private bool AcceptSession(long session) => !_disposed && session != 0 && session == _activeSessionId &&
        !_suppressActiveEvents && _requestCancellation?.IsCancellationRequested != true;
    private bool TryUseRetry(Intent intent)
    {
        lock (_sync) { if (!IsCurrentUnsafe(intent) || _retryUsed) return false; _retryUsed = true; return true; }
    }

    private void OnState(object? sender, AudioSessionState state)
    {
        lock (_sync)
        {
            if (!AcceptSession(state.SessionId)) return;
            Publish(_snapshot with { State = state.State,
                Duration = _audio.Duration > TimeSpan.Zero ? _audio.Duration : _snapshot.Duration });
        }
    }

    private void OnPosition(object? sender, AudioSessionPosition position)
    {
        lock (_sync) if (AcceptSession(position.SessionId))
            Publish(_snapshot with { Position = ClampPosition(position.Position, _snapshot.Duration) });
    }

    private void OnFailure(object? sender, AudioSessionFailure failure)
    {
        Intent intent;
        Track? track;
        AudioSource? source;
        TimeSpan position;
        lock (_sync)
        {
            if (!AcceptSession(failure.SessionId)) return;
            if (_loading) { _loadingError = failure.Error; return; }
            intent = CurrentIntent();
            track = _snapshot.Track;
            source = _source;
            position = _desiredPosition ?? _snapshot.Position;
            Publish(_snapshot with { State = PlaybackState.Error, CanSeek = false, Message = failure.Error.Message });
            _suppressActiveEvents = true;
            if (track is null || source?.IsRemote != true || !TryUseRetry(intent)) return;
        }
        _ = RetryActiveAsync(intent, track, source, position);
    }

    private async Task RetryActiveAsync(Intent intent, Track track, AudioSource source, TimeSpan position)
    {
        try
        {
            var resolution = await ResolveWithRetryAsync(intent, track).ConfigureAwait(false);
            if (!IsCurrent(intent)) return;
            if (!ApplyResolution(intent, track, resolution)) return;
            var rejection = ResolutionError(resolution, source.IsPreview);
            if (rejection is not null) { FailAfterSwitch(intent, track, rejection); return; }
            var result = await LoadAttemptAsync(intent, track, NormalizeSource(resolution), null,
                source.CanSeek ? position : null).ConfigureAwait(false);
            if (result.Started) DeliverDeferredCompletion(result.SessionId);
        }
        catch (OperationCanceledException) when (intent.Token.IsCancellationRequested) { }
        catch (Exception ex) { FailAfterSwitch(intent, track, ex.Message); }
    }

    private void OnCompletion(object? sender, AudioSessionCompletion completion)
    {
        Intent? previewStop = null;
        lock (_sync)
        {
            if (!AcceptSession(completion.SessionId) || !_wantsPlaying || _completedSessionId == completion.SessionId) return;
            if (_loading) { _deferredCompletion = completion; return; }
            _completedSessionId = completion.SessionId;
            if (_snapshot.IsPreview || completion.IsPreview)
            {
                _wantsPlaying = false;
                _activeSessionId = 0;
                CancelLyricsUnsafe(clearLoading: true);
                _source = null;
                _desiredPosition = null;
                Publish(_snapshot with { State = PlaybackState.Stopped, CanSeek = false, Message = "试听结束。" });
                previewStop = CurrentIntent();
            }
        }
        if (previewStop is not null) { _ = StopAudioAsync(previewStop, unload: true); return; }
        _ = NavigateAsync(previous: false, completed: true, expectedSession: completion.SessionId);
    }

    private void DeliverDeferredCompletion(long session)
    {
        AudioSessionCompletion? completion;
        lock (_sync)
        {
            completion = _deferredCompletion;
            if (completion is null || completion.SessionId != session) return;
            _deferredCompletion = null;
        }
        OnCompletion(this, completion);
    }

    private async Task StopAudioAsync(Intent intent, bool unload)
    {
        try
        {
            await _audioGate.WaitAsync(intent.Token).ConfigureAwait(false);
            try
            {
                if (!IsCurrent(intent)) return;
                if (unload) await _audio.UnloadAsync().ConfigureAwait(false);
                else await _audio.StopAsync().ConfigureAwait(false);
            }
            finally { _audioGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (_sync) if (IsCurrentUnsafe(intent)) Publish(_snapshot with { State = PlaybackState.Error, Message = ex.Message });
        }
    }

    private void SetActiveError(Intent intent, long session, string message)
    { lock (_sync) if (IsActiveUnsafe(intent, session)) Publish(_snapshot with { State = PlaybackState.Error, Message = message }); }
    private static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration) => position < TimeSpan.Zero
        ? TimeSpan.Zero : duration > TimeSpan.Zero && position > duration ? duration : position;
    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        // A completed provider/FFProbe can retain a callback against a disposed handle.
        // Cancellation still reaches every callback; generation checks own stale results.
        catch (AggregateException) { }
        catch (ObjectDisposedException) { }
        finally { source.Dispose(); }
    }
    private void Publish(PlaybackSnapshot snapshot) { _snapshot = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void OnLegacyState(object? sender, PlaybackState state) => OnState(sender, new(_activeSessionId, state));
    private void OnLegacyPosition(object? sender, TimeSpan position) => OnPosition(sender, new(_activeSessionId, position));
    private void OnLegacyFailure(object? sender, Exception error) => OnFailure(sender, new(_activeSessionId, error));
    private void OnLegacyCompletion(object? sender, EventArgs args) => OnCompletion(sender, new(_activeSessionId, _source?.IsPreview ?? false));

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _activeSessionId = 0;
            CancelLyricsUnsafe();
            cancellation = _requestCancellation;
            _requestCancellation = null;
        }
        CancelAndDispose(cancellation);
        if (_sessionAudio is not null)
        {
            _sessionAudio.SessionStateChanged -= OnState;
            _sessionAudio.SessionPositionChanged -= OnPosition;
            _sessionAudio.SessionPlaybackFailed -= OnFailure;
            _sessionAudio.SessionPlaybackCompleted -= OnCompletion;
        }
        else
        {
            _audio.StateChanged -= OnLegacyState;
            _audio.PositionChanged -= OnLegacyPosition;
            _audio.PlaybackFailed -= OnLegacyFailure;
            _audio.PlaybackCompleted -= OnLegacyCompletion;
        }
        await _audioGate.WaitAsync().ConfigureAwait(false);
        try { await _audio.UnloadAsync().ConfigureAwait(false); }
        finally { _audioGate.Release(); }
    }

    private sealed record Intent(long Generation, CancellationToken Token);
    private sealed record LyricsRequest(long Generation, CancellationToken Token);
}
