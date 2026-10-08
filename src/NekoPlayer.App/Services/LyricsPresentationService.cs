using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;

namespace NekoPlayer.App.Services;

/// <summary>One UI-thread lyric clock shared by the main player and desktop lyrics.</summary>
public sealed class LyricsPresentationService : ObservableObject, IDisposable
{
    private readonly object _gate = new();
    private readonly PlaybackCoordinator? _playback;
    private readonly Action<Action> _post;
    private readonly Func<CancellationToken, Task>? _retry;
    private readonly LyricPositionTracker _tracker = new();
    private PlaybackSnapshot? _latestSnapshot;
    private PlaybackLyrics? _pendingLyrics;
    private bool _pending;
    private bool _dirty;
    private bool _disposed;
    private bool _forceImmediate;
    private bool _retainLoadedRows;
    private bool _isPreview;
    private long _sessionId;
    private Guid _trackId;
    private TimeSpan _position;
    private long _positionTick;
    private IReadOnlyList<LyricsPresentationRow> _rows = Array.Empty<LyricsPresentationRow>();
    private int _currentIndex = -1;
    private string _currentText = string.Empty;
    private string _nextText = string.Empty;
    private LyricsLoadingState _status;
    private string? _message;
    private bool _canSeek;

    public LyricsPresentationService(PlaybackCoordinator playback, Action<Action>? post = null)
        : this(post ?? (callback => Dispatcher.UIThread.Post(callback, DispatcherPriority.Background)),
            playback.RetryLyricsAsync)
    {
        _playback = playback;
        playback.SnapshotChanged += OnSnapshot;
        playback.LyricsChanged += OnLyrics;
        SubmitSnapshot(playback.Snapshot);
    }

    /// <summary>The injected dispatcher makes coalescing deterministic in tests.</summary>
    public LyricsPresentationService(Action<Action> post, Func<CancellationToken, Task>? retry = null)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _retry = retry;
        RetryCommand = new AsyncRelayCommand(() => RetryLyricsAsync(), () => CanRetry);
    }

    public IReadOnlyList<LyricsPresentationRow> Rows => _rows;
    public int CurrentIndex => _currentIndex;
    public string CurrentText => _currentText;
    public string NextText => _nextText;
    public LyricsLoadingState Status => _status;
    public string? Message => _message;
    public bool CanSeek => _canSeek;
    public long SessionId => _sessionId;
    public Guid TrackId => _trackId;
    public TimeSpan Position => _position;
    public bool HasLyrics => _rows.Count != 0;
    public bool IsLoading => Status == LyricsLoadingState.Loading;
    public bool IsError => Status == LyricsLoadingState.Error;
    public bool CanRetry => !_disposed && _trackId != Guid.Empty && _retry is not null &&
        Status is LyricsLoadingState.Empty or LyricsLoadingState.Error;
    public string StatusText => Status switch
    {
        LyricsLoadingState.Loading => "正在读取歌词……",
        LyricsLoadingState.Empty => "暂未找到歌词",
        LyricsLoadingState.Error => string.IsNullOrWhiteSpace(Message) ? "歌词读取失败" : Message!,
        LyricsLoadingState.Ready => "歌词已就绪",
        _ => "选择歌曲后显示歌词"
    };
    public IAsyncRelayCommand RetryCommand { get; }
    public event EventHandler<LyricsCurrentChangedEventArgs>? CurrentChanged;

    public Task RetryLyricsAsync(CancellationToken cancellationToken = default) =>
        CanRetry ? _retry!(cancellationToken) : Task.CompletedTask;

    /// <summary>Seek callers use this even for a seek inside the currently highlighted line.</summary>
    public void RequestImmediateFollow()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _forceImmediate = true;
        }
        _post(() =>
        {
            if (!_disposed) CurrentChanged?.Invoke(this, new(CurrentIndex, CurrentIndex, true));
        });
    }

    public void SubmitSnapshot(PlaybackSnapshot snapshot)
    {
        var schedule = false;
        lock (_gate)
        {
            if (_disposed || (_latestSnapshot is { } latest && snapshot.SessionId < latest.SessionId)) return;
            _latestSnapshot = snapshot;
            if (_pendingLyrics is { } lyrics &&
                (lyrics.SessionId != snapshot.SessionId || lyrics.TrackId != (snapshot.Track?.Id ?? Guid.Empty)))
                _pendingLyrics = null;
            _dirty = true;
            if (!_pending) { _pending = true; schedule = true; }
        }
        if (schedule) PostDelivery();
    }

    public void SubmitLyrics(PlaybackLyrics lyrics)
    {
        var schedule = false;
        lock (_gate)
        {
            if (_disposed || _latestSnapshot is not { } snapshot || lyrics.SessionId != snapshot.SessionId ||
                lyrics.TrackId != (snapshot.Track?.Id ?? Guid.Empty)) return;
            // A single immutable delivery replaces prior work while the UI is busy.
            _pendingLyrics = lyrics with { Lines = lyrics.Lines.ToArray() };
            _dirty = true;
            if (!_pending) { _pending = true; schedule = true; }
        }
        if (schedule) PostDelivery();
    }

    private void OnSnapshot(object? sender, PlaybackSnapshot snapshot) => SubmitSnapshot(snapshot);
    private void OnLyrics(object? sender, PlaybackLyrics lyrics) => SubmitLyrics(lyrics);

    private void PostDelivery()
    {
        try { _post(DeliverLatest); }
        catch { lock (_gate) _pending = false; throw; }
    }

    private void DeliverLatest()
    {
        PlaybackSnapshot? snapshot;
        PlaybackLyrics? lyrics;
        bool immediate;
        lock (_gate)
        {
            if (_disposed) { _pending = false; return; }
            snapshot = _latestSnapshot;
            lyrics = _pendingLyrics;
            _pendingLyrics = null;
            _dirty = false;
            immediate = _forceImmediate;
            _forceImmediate = false;
        }
        try
        {
            if (snapshot is null || snapshot.SessionId < _sessionId) return;
            immediate |= ApplySnapshot(snapshot);
            if (lyrics is not null && lyrics.SessionId == _sessionId && lyrics.TrackId == _trackId)
                immediate |= ApplyLyrics(lyrics);
            UpdateCurrent(snapshot.Position, immediate);
        }
        finally
        {
            var schedule = false;
            lock (_gate)
            {
                if (!_disposed && _dirty) schedule = true;
                else _pending = false;
            }
            if (schedule) PostDelivery();
        }
    }

    private bool ApplySnapshot(PlaybackSnapshot snapshot)
    {
        var id = snapshot.Track?.Id ?? Guid.Empty;
        var changed = snapshot.SessionId != _sessionId || id != _trackId;
        if (changed)
        {
            _retainLoadedRows = id != Guid.Empty && id == _trackId && !_isPreview && !snapshot.IsPreview && HasLyrics;
            _sessionId = snapshot.SessionId;
            _trackId = id;
            _isPreview = snapshot.IsPreview;
            OnPropertyChanged(nameof(SessionId));
            OnPropertyChanged(nameof(TrackId));
            if (!_retainLoadedRows)
            {
                ReplaceRows(Array.Empty<LyricsPresentationRow>());
                SetStatus(id == Guid.Empty ? LyricsLoadingState.None : LyricsLoadingState.Loading, null);
            }
            else
            {
                _tracker.Reset(Rows.Select(row => row.Timestamp));
                SetStatus(LyricsLoadingState.Ready, null);
            }
            NotifyRetryChanged();
        }
        SetProperty(ref _canSeek, snapshot.CanSeek && snapshot.PendingTrack is null &&
            snapshot.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering, nameof(CanSeek));
        var elapsed = _positionTick == 0 ? 0 : Stopwatch.GetElapsedTime(_positionTick).TotalSeconds;
        var jumped = snapshot.Position < _position || (snapshot.Position - _position).TotalSeconds > elapsed + 0.65;
        _position = snapshot.Position;
        _positionTick = Stopwatch.GetTimestamp();
        // Position itself is deliberately not notified at the audio timer's frequency.
        return changed || jumped;
    }

    private bool ApplyLyrics(PlaybackLyrics lyrics)
    {
        var state = lyrics.State == LyricsLoadingState.None && _trackId != Guid.Empty
            ? (lyrics.Lines.Count > 0 ? LyricsLoadingState.Ready : LyricsLoadingState.Empty) : lyrics.State;
        if (state == LyricsLoadingState.Loading)
        {
            if (!_retainLoadedRows) SetStatus(state, lyrics.Message);
            return false;
        }
        if (state == LyricsLoadingState.Ready)
        {
            var lines = lyrics.Lines.OrderBy(line => line.Timestamp).ToArray();
            if (lines.Length == 0) state = LyricsLoadingState.Empty;
            else
            {
                var same = lines.Length == Rows.Count && lines.Select((line, index) =>
                    line.Timestamp == Rows[index].Timestamp && line.Text == Rows[index].Text).All(value => value);
                if (!same) ReplaceRows(lines.Select(line => new LyricsPresentationRow(line)).ToArray());
                _retainLoadedRows = false;
                SetStatus(state, lyrics.Message);
                return !same;
            }
        }
        // Retain an already rendered full-track lyric if its repeat fetch fails.
        if (!_retainLoadedRows) ReplaceRows(Array.Empty<LyricsPresentationRow>());
        _retainLoadedRows = false;
        SetStatus(state, lyrics.Message);
        return true;
    }

    private void ReplaceRows(IReadOnlyList<LyricsPresentationRow> rows)
    {
        if (ReferenceEquals(rows, _rows)) return;
        if (_currentIndex >= 0 && _currentIndex < _rows.Count) _rows[_currentIndex].IsCurrent = false;
        _rows = rows;
        _tracker.Reset(rows.Select(row => row.Timestamp));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HasLyrics));
    }

    private void UpdateCurrent(TimeSpan position, bool immediate)
    {
        var next = _tracker.Update(position, immediate).CurrentIndex;
        var previous = _currentIndex;
        if (previous != next)
        {
            if (previous >= 0 && previous < Rows.Count) Rows[previous].IsCurrent = false;
            if (next >= 0 && next < Rows.Count) Rows[next].IsCurrent = true;
            SetProperty(ref _currentIndex, next, nameof(CurrentIndex));
        }
        else if (next >= 0 && next < Rows.Count && !Rows[next].IsCurrent)
            Rows[next].IsCurrent = true;
        SetProperty(ref _currentText, next >= 0 && next < Rows.Count ? Rows[next].Text : string.Empty, nameof(CurrentText));
        SetProperty(ref _nextText, next + 1 >= 0 && next + 1 < Rows.Count ? Rows[next + 1].Text : string.Empty, nameof(NextText));
        if (previous != next || immediate) CurrentChanged?.Invoke(this, new(previous, next, immediate));
    }

    private void SetStatus(LyricsLoadingState status, string? message)
    {
        var changed = SetProperty(ref _status, status, nameof(Status));
        var messageChanged = SetProperty(ref _message, message, nameof(Message));
        if (changed)
        {
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsError));
        }
        if (changed || messageChanged) OnPropertyChanged(nameof(StatusText));
        NotifyRetryChanged();
    }

    private void NotifyRetryChanged()
    {
        OnPropertyChanged(nameof(CanRetry));
        RetryCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingLyrics = null;
            _latestSnapshot = null;
        }
        if (_playback is not null)
        {
            _playback.SnapshotChanged -= OnSnapshot;
            _playback.LyricsChanged -= OnLyrics;
        }
    }
}

public sealed class LyricsPresentationRow(LyricsLine line) : ObservableObject
{
    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.Parse("#A8C1C4"));
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.Parse("#5EE8DE"));
    private bool _isCurrent;
    public TimeSpan Timestamp => line.Timestamp;
    public string Text => line.Text;
    public IBrush Foreground => IsCurrent ? CurrentBrush : NormalBrush;
    public FontWeight FontWeight => IsCurrent ? FontWeight.SemiBold : FontWeight.Normal;
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (!SetProperty(ref _isCurrent, value)) return;
            OnPropertyChanged(nameof(Foreground));
            OnPropertyChanged(nameof(FontWeight));
        }
    }
}

public sealed class LyricsCurrentChangedEventArgs(int previousIndex, int currentIndex, bool immediate) : EventArgs
{
    public int PreviousIndex { get; } = previousIndex;
    public int CurrentIndex { get; } = currentIndex;
    public bool Immediate { get; } = immediate;
}
