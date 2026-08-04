using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Core.Interfaces;

public interface IPlaybackQueueService
{
    IReadOnlyList<Track> Items { get; }
    int CurrentIndex { get; }
    Track? Current { get; }
    PlayMode PlayMode { get; set; }
    void Replace(IEnumerable<Track> tracks, Guid? currentTrackId = null);
    void Restore(IEnumerable<Track> tracks, int currentIndex);
    void Add(Track track);
    void PlayNext(Track track);
    void Remove(Guid trackId);
    void Clear();
    void ClearCurrent();
    Track? SetCurrent(Guid trackId);
    Track? MoveNext(bool playbackCompleted = false);
    Track? MovePrevious(TimeSpan currentPosition);
    event EventHandler? QueueChanged;
    event EventHandler<Track?>? CurrentChanged;
}
