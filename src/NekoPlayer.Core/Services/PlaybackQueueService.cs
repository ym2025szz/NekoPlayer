using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Core.Services;

public sealed class PlaybackQueueService : IPlaybackQueueService
{
    private readonly object _sync = new();
    private readonly List<Track> _items = [];
    private readonly Random _random;
    private int _currentIndex = -1;
    private PlayMode _playMode = PlayMode.RepeatAll;

    public PlaybackQueueService() : this(Random.Shared) { }
    internal PlaybackQueueService(Random random) => _random = random;

    // Expose a stable snapshot instead of a collection being mutated by commands.
    public IReadOnlyList<Track> Items { get { lock (_sync) return _items.ToArray(); } }
    public int CurrentIndex { get { lock (_sync) return _currentIndex; } }
    public Track? Current { get { lock (_sync) return CurrentUnsafe; } }
    public PlayMode PlayMode { get { lock (_sync) return _playMode; } set { lock (_sync) _playMode = value; } }
    public event EventHandler? QueueChanged;
    public event EventHandler<Track?>? CurrentChanged;

    private Track? CurrentUnsafe => _currentIndex >= 0 && _currentIndex < _items.Count ? _items[_currentIndex] : null;

    public void Replace(IEnumerable<Track> tracks, Guid? currentTrackId = null)
    {
        var snapshot = tracks.DistinctBy(x => x.Id).ToArray();
        lock (_sync)
        {
            _items.Clear();
            _items.AddRange(snapshot);
            _currentIndex = currentTrackId is null ? (_items.Count > 0 ? 0 : -1) : _items.FindIndex(x => x.Id == currentTrackId);
            if (_currentIndex < 0 && _items.Count > 0) _currentIndex = 0;
        }
        NotifyAll();
    }

    public void Restore(IEnumerable<Track> tracks, int currentIndex)
    {
        var snapshot = tracks.DistinctBy(x => x.Id).ToArray();
        lock (_sync)
        {
            _items.Clear();
            _items.AddRange(snapshot);
            _currentIndex = _items.Count == 0 || currentIndex < 0 ? -1 : Math.Clamp(currentIndex, 0, _items.Count - 1);
        }
        NotifyAll();
    }

    public void Add(Track track)
    {
        lock (_sync)
        {
            if (_items.Any(x => x.Id == track.Id)) return;
            _items.Add(track);
            if (_currentIndex < 0) _currentIndex = 0;
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PlayNext(Track track)
    {
        lock (_sync)
        {
            var anchorId = CurrentUnsafe?.Id;
            if (anchorId == track.Id) return;
            _items.RemoveAll(x => x.Id == track.Id);
            var anchorIndex = anchorId is { } id ? _items.FindIndex(x => x.Id == id) : -1;
            _items.Insert(anchorIndex < 0 ? 0 : anchorIndex + 1, track);
            _currentIndex = anchorId is { } currentId ? _items.FindIndex(x => x.Id == currentId) : 0;
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(Guid trackId)
    {
        lock (_sync)
        {
            var index = _items.FindIndex(x => x.Id == trackId);
            if (index < 0) return;
            var anchorId = CurrentUnsafe?.Id;
            _items.RemoveAt(index);
            _currentIndex = anchorId is { } id && id != trackId ? _items.FindIndex(x => x.Id == id) : -1;
        }
        NotifyAll();
    }

    public void Move(Guid trackId, int targetIndex)
    {
        lock (_sync)
        {
            var index = _items.FindIndex(x => x.Id == trackId);
            if (index < 0) return;
            targetIndex = Math.Clamp(targetIndex, 0, _items.Count - 1);
            if (index == targetIndex) return;
            var currentId = CurrentUnsafe?.Id;
            var track = _items[index];
            _items.RemoveAt(index);
            _items.Insert(targetIndex, track);
            _currentIndex = currentId is { } id ? _items.FindIndex(x => x.Id == id) : -1;
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearPending()
    {
        lock (_sync)
        {
            var current = CurrentUnsafe;
            if (current is null && _items.Count == 0 || current is not null && _items.Count == 1) return;
            _items.Clear();
            if (current is not null) _items.Add(current);
            _currentIndex = current is null ? -1 : 0;
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_sync) { _items.Clear(); _currentIndex = -1; }
        NotifyAll();
    }

    public void ClearCurrent()
    {
        lock (_sync) { _currentIndex = -1; }
        NotifyAll();
    }

    public Track? SetCurrent(Guid trackId)
    {
        Track? current;
        lock (_sync)
        {
            var index = _items.FindIndex(x => x.Id == trackId);
            if (index < 0) return null;
            _currentIndex = index;
            current = CurrentUnsafe;
        }
        CurrentChanged?.Invoke(this, current);
        return current;
    }

    public Track? MoveNext(bool playbackCompleted = false)
    {
        Track? current;
        lock (_sync)
        {
            if (_items.Count == 0) return null;
            if (_playMode == PlayMode.RepeatOne && playbackCompleted) current = CurrentUnsafe;
            else
            {
                var next = _playMode switch
                {
                    PlayMode.Shuffle when _items.Count > 1 => NextRandomIndex(),
                    PlayMode.RepeatAll => (_currentIndex + 1) % _items.Count,
                    PlayMode.Sequential when _currentIndex + 1 >= _items.Count => -1,
                    _ => Math.Min(_currentIndex + 1, _items.Count - 1)
                };
                if (next < 0) return null;
                _currentIndex = next;
                current = CurrentUnsafe;
            }
        }
        CurrentChanged?.Invoke(this, current);
        return current;
    }

    public Track? MovePrevious(TimeSpan currentPosition)
    {
        Track? current;
        lock (_sync)
        {
            if (_items.Count == 0) return null;
            if (currentPosition <= TimeSpan.FromSeconds(3))
                _currentIndex = _currentIndex <= 0 ? (_playMode == PlayMode.RepeatAll ? _items.Count - 1 : 0) : _currentIndex - 1;
            current = CurrentUnsafe;
        }
        CurrentChanged?.Invoke(this, current);
        return current;
    }

    private int NextRandomIndex()
    {
        var next = _currentIndex;
        for (var i = 0; i < 8 && next == _currentIndex; i++) next = _random.Next(_items.Count);
        return next == _currentIndex ? (_currentIndex + 1) % _items.Count : next;
    }

    private void NotifyAll()
    {
        QueueChanged?.Invoke(this, EventArgs.Empty);
        CurrentChanged?.Invoke(this, Current);
    }
}
