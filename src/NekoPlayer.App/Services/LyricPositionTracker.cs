namespace NekoPlayer.App.Services;

public readonly record struct LyricPositionChange(int PreviousIndex, int CurrentIndex)
{
    public bool HasChanged => PreviousIndex != CurrentIndex;
}

/// <summary>Tracks the last lyric timestamp at or before playback position.</summary>
public sealed class LyricPositionTracker
{
    private TimeSpan[] _timestamps = [];
    private TimeSpan? _lastPosition;
    public int CurrentIndex { get; private set; } = -1;

    public void Reset(IEnumerable<TimeSpan> orderedTimestamps)
    {
        ArgumentNullException.ThrowIfNull(orderedTimestamps);
        var timestamps = orderedTimestamps.ToArray();
        for (var i = 1; i < timestamps.Length; i++)
            if (timestamps[i] < timestamps[i - 1])
                throw new ArgumentException("歌词时间戳必须按时间排序。", nameof(orderedTimestamps));
        _timestamps = timestamps;
        _lastPosition = null;
        CurrentIndex = -1;
    }

    public LyricPositionChange Update(TimeSpan position, bool isSeek = false)
    {
        var previous = CurrentIndex;
        if (!isSeek && _lastPosition is { } last && position >= last && position - last <= TimeSpan.FromSeconds(2))
        {
            // Ordinary playback advances from the current line without rescanning earlier lyrics.
            while (CurrentIndex + 1 < _timestamps.Length && _timestamps[CurrentIndex + 1] <= position)
                CurrentIndex++;
        }
        else
        {
            // Initial position, backward movement, and large jumps use an upper-bound binary search.
            var low = 0;
            var high = _timestamps.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_timestamps[middle] <= position) low = middle + 1;
                else high = middle;
            }
            CurrentIndex = low - 1;
        }
        _lastPosition = position;
        return new LyricPositionChange(previous, CurrentIndex);
    }
}
