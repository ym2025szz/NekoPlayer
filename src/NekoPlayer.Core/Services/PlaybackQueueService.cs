using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Core.Services;

public sealed class PlaybackQueueService : IPlaybackQueueService
{
    private readonly List<Track> _items = [];
    private readonly Random _random;
    private int _currentIndex = -1;

    public PlaybackQueueService() : this(Random.Shared) { }
    internal PlaybackQueueService(Random random) => _random = random;

    public IReadOnlyList<Track> Items => _items;
    public int CurrentIndex => _currentIndex;
    public Track? Current => _currentIndex >= 0 && _currentIndex < _items.Count ? _items[_currentIndex] : null;
    public PlayMode PlayMode { get; set; } = PlayMode.RepeatAll;
    public event EventHandler? QueueChanged;
    public event EventHandler<Track?>? CurrentChanged;

    public void Replace(IEnumerable<Track> tracks, Guid? currentTrackId = null)
    {
        _items.Clear();
        _items.AddRange(tracks.DistinctBy(x => x.Id));
        _currentIndex = currentTrackId is null ? (_items.Count > 0 ? 0 : -1) : _items.FindIndex(x => x.Id == currentTrackId);
        if (_currentIndex < 0 && _items.Count > 0) _currentIndex = 0;
        NotifyAll();
    }

    public void Restore(IEnumerable<Track> tracks, int currentIndex)
    {
        _items.Clear();
        _items.AddRange(tracks.DistinctBy(x => x.Id));
        _currentIndex = _items.Count == 0 ? -1 : Math.Clamp(currentIndex, 0, _items.Count - 1);
        NotifyAll();
    }

    public void Add(Track track)
    {
        if (_items.Any(x => x.Id == track.Id)) return;
        _items.Add(track);
        if (_currentIndex < 0) _currentIndex = 0;
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PlayNext(Track track)
    {
        _items.RemoveAll(x => x.Id == track.Id);
        var insertAt = _currentIndex < 0 ? 0 : Math.Min(_currentIndex + 1, _items.Count);
        _items.Insert(insertAt, track);
        if (_currentIndex < 0) _currentIndex = 0;
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(Guid trackId)
    {
        var index = _items.FindIndex(x => x.Id == trackId);
        if (index < 0) return;
        _items.RemoveAt(index);
        if (_items.Count == 0) _currentIndex = -1;
        else if (index < _currentIndex) _currentIndex--;
        else if (_currentIndex >= _items.Count) _currentIndex = _items.Count - 1;
        NotifyAll();
    }

    public void Clear()
    {
        _items.Clear();
        _currentIndex = -1;
        NotifyAll();
    }

    public void ClearCurrent()
    {
        _currentIndex = -1;
        QueueChanged?.Invoke(this, EventArgs.Empty);
        CurrentChanged?.Invoke(this, null);
    }

    public Track? SetCurrent(Guid trackId)
    {
        var index = _items.FindIndex(x => x.Id == trackId);
        if (index < 0) return null;
        _currentIndex = index;
        CurrentChanged?.Invoke(this, Current);
        return Current;
    }

    public Track? MoveNext(bool playbackCompleted = false)
    {
        if (_items.Count == 0) return null;
        if (PlayMode == PlayMode.RepeatOne && playbackCompleted) return NotifyCurrent();

        var next = PlayMode switch
        {
            PlayMode.Shuffle when _items.Count > 1 => NextRandomIndex(),
            PlayMode.RepeatAll => (_currentIndex + 1) % _items.Count,
            PlayMode.Sequential when _currentIndex + 1 >= _items.Count => -1,
            _ => Math.Min(_currentIndex + 1, _items.Count - 1)
        };

        if (next < 0) return null;
        _currentIndex = next;
        return NotifyCurrent();
    }

    public Track? MovePrevious(TimeSpan currentPosition)
    {
        if (_items.Count == 0) return null;
        if (currentPosition > TimeSpan.FromSeconds(3)) return NotifyCurrent();
        _currentIndex = _currentIndex <= 0
            ? (PlayMode == PlayMode.RepeatAll ? _items.Count - 1 : 0)
            : _currentIndex - 1;
        return NotifyCurrent();
    }

    private int NextRandomIndex()
    {
        var next = _currentIndex;
        for (var i = 0; i < 8 && next == _currentIndex; i++) next = _random.Next(_items.Count);
        return next == _currentIndex ? (_currentIndex + 1) % _items.Count : next;
    }

    private Track? NotifyCurrent()
    {
        var current = Current;
        CurrentChanged?.Invoke(this, current);
        return current;
    }

    private void NotifyAll()
    {
        QueueChanged?.Invoke(this, EventArgs.Empty);
        CurrentChanged?.Invoke(this, Current);
    }
}
