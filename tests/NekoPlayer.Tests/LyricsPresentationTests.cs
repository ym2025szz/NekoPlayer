using NekoPlayer.App.Services;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Tests;

public sealed class LyricsPresentationTests
{
    [Fact]
    public void AudioTicksAreMergedAndOnlyOldAndNewRowsChangeHighlight()
    {
        var queue = new Queue<Action>();
        using var service = new LyricsPresentationService(queue.Enqueue);
        var track = new Track { Id = Guid.NewGuid(), Title = "long lyric" };
        service.SubmitSnapshot(Snapshot(1, track, 0));
        service.SubmitLyrics(new(1, track.Id,
            Enumerable.Range(0, 5000).Select(index => new LyricsLine(TimeSpan.FromSeconds(index), $"line {index}")).ToArray(),
            LyricsLoadingState.Ready));
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(5000, service.Rows.Count);
        Assert.Equal(0, service.CurrentIndex);
        var rows = service.Rows;
        var changed = new List<int>();
        for (var index = 0; index < rows.Count; index++)
        {
            var rowIndex = index;
            rows[index].PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LyricsPresentationRow.IsCurrent)) changed.Add(rowIndex);
            };
        }
        var currentEvents = 0;
        service.CurrentChanged += (_, _) => currentEvents++;
        for (var index = 0; index < 10; index++) service.SubmitSnapshot(Snapshot(1, track, index / 20d));
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Empty(changed);
        Assert.Equal(0, currentEvents);
        service.SubmitSnapshot(Snapshot(1, track, 4000));
        queue.Dequeue()();
        Assert.Equal(new[] { 0, 4000 }, changed);
        Assert.Same(rows, service.Rows);
        Assert.Equal("line 4000", service.CurrentText);
        Assert.Equal("line 4001", service.NextText);
    }

    [Fact]
    public void NewTrackRejectsDelayedOldLyricsAndOldSnapshots()
    {
        var queue = new Queue<Action>();
        using var service = new LyricsPresentationService(queue.Enqueue);
        var first = new Track { Id = Guid.NewGuid() };
        var second = new Track { Id = Guid.NewGuid() };
        service.SubmitSnapshot(Snapshot(1, first, 0));
        service.SubmitLyrics(new(1, first.Id, [new(TimeSpan.Zero, "old")], LyricsLoadingState.Ready));
        service.SubmitSnapshot(Snapshot(2, second, 0));
        service.SubmitLyrics(new(2, second.Id, [new(TimeSpan.Zero, "new")], LyricsLoadingState.Ready));
        service.SubmitLyrics(new(1, first.Id, [new(TimeSpan.Zero, "late")], LyricsLoadingState.Ready));
        service.SubmitSnapshot(Snapshot(1, first, 100));
        queue.Dequeue()();
        Assert.Equal(second.Id, service.TrackId);
        Assert.Equal("new", service.CurrentText);
        Assert.Single(service.Rows);
    }

    [Fact]
    public void CompleteTrackRepeatResetsClockWithoutClearingLoadedRows()
    {
        var queue = new Queue<Action>();
        using var service = new LyricsPresentationService(queue.Enqueue);
        var track = new Track { Id = Guid.NewGuid() };
        var lines = new LyricsLine[] { new(TimeSpan.Zero, "first"), new(TimeSpan.FromSeconds(10), "last") };
        service.SubmitSnapshot(Snapshot(1, track, 15));
        service.SubmitLyrics(new(1, track.Id, lines, LyricsLoadingState.Ready));
        queue.Dequeue()();
        var rows = service.Rows;
        Assert.True(rows[1].IsCurrent);
        service.SubmitSnapshot(Snapshot(2, track, 0) with { State = PlaybackState.Loading });
        service.SubmitLyrics(new(2, track.Id, [], LyricsLoadingState.Loading));
        queue.Dequeue()();
        Assert.Same(rows, service.Rows);
        Assert.Equal(LyricsLoadingState.Ready, service.Status);
        Assert.Equal(0, service.CurrentIndex);
        Assert.True(rows[0].IsCurrent);
        Assert.False(rows[1].IsCurrent);
        service.SubmitSnapshot(Snapshot(2, track, 0));
        service.SubmitLyrics(new(2, track.Id, lines, LyricsLoadingState.Ready));
        queue.Dequeue()();
        Assert.Same(rows, service.Rows);
    }

    [Fact]
    public void PreviewSessionsUseDeliveredSegmentTimestampsAndDoNotRetainFullLyrics()
    {
        var queue = new Queue<Action>();
        using var service = new LyricsPresentationService(queue.Enqueue);
        var track = new Track { Id = Guid.NewGuid() };
        service.SubmitSnapshot(Snapshot(1, track, 90));
        service.SubmitLyrics(new(1, track.Id, [new(TimeSpan.FromSeconds(90), "full")], LyricsLoadingState.Ready));
        queue.Dequeue()();
        service.SubmitSnapshot(Snapshot(2, track, 2) with { IsPreview = true });
        service.SubmitLyrics(new(2, track.Id, [], LyricsLoadingState.Loading));
        queue.Dequeue()();
        Assert.Empty(service.Rows);
        Assert.Equal(LyricsLoadingState.Loading, service.Status);
        service.SubmitLyrics(new(2, track.Id,
            [new(TimeSpan.FromSeconds(1), "segment"), new(TimeSpan.FromSeconds(5), "next")], LyricsLoadingState.Ready));
        queue.Dequeue()();
        Assert.Equal(TimeSpan.FromSeconds(1), service.Rows[0].Timestamp);
        Assert.Equal("segment", service.CurrentText);
        Assert.Equal("next", service.NextText);
    }

    [Fact]
    public async Task EmptyAndFailedLyricsHaveIndependentStateAndRetry()
    {
        var queue = new Queue<Action>();
        var retries = 0;
        using var service = new LyricsPresentationService(queue.Enqueue, _ => { retries++; return Task.CompletedTask; });
        var track = new Track { Id = Guid.NewGuid() };
        service.SubmitSnapshot(Snapshot(1, track, 0));
        service.SubmitLyrics(new(1, track.Id, [], LyricsLoadingState.Empty));
        queue.Dequeue()();
        Assert.Empty(service.Rows);
        Assert.True(service.CanRetry);
        Assert.True(service.CanSeek);
        await service.RetryLyricsAsync();
        Assert.Equal(1, retries);
        service.SubmitLyrics(new(1, track.Id, [], LyricsLoadingState.Loading));
        queue.Dequeue()();
        Assert.True(service.IsLoading);
        Assert.False(service.CanRetry);
        service.SubmitLyrics(new(1, track.Id, [], LyricsLoadingState.Error, "network unavailable"));
        queue.Dequeue()();
        Assert.True(service.IsError);
        Assert.Equal("network unavailable", service.StatusText);
        Assert.Empty(service.Rows);
        Assert.Equal(string.Empty, service.CurrentText);
        Assert.Equal(string.Empty, service.NextText);
    }

    [Fact]
    public void SeekWithinOneLineStillRequestsImmediateFollowAndKeepsPausedSeekEnabled()
    {
        var queue = new Queue<Action>();
        using var service = new LyricsPresentationService(queue.Enqueue);
        var track = new Track { Id = Guid.NewGuid() };
        service.SubmitSnapshot(Snapshot(1, track, 1) with { State = PlaybackState.Paused });
        service.SubmitLyrics(new(1, track.Id, [new(TimeSpan.Zero, "line")], LyricsLoadingState.Ready));
        queue.Dequeue()();
        var immediate = new List<bool>();
        service.CurrentChanged += (_, args) => immediate.Add(args.Immediate);
        service.RequestImmediateFollow();
        queue.Dequeue()();
        service.SubmitSnapshot(Snapshot(1, track, 1.1) with { State = PlaybackState.Paused });
        queue.Dequeue()();
        Assert.True(service.CanSeek);
        Assert.Equal(new[] { true, true }, immediate);
        service.SubmitSnapshot(Snapshot(1, track, 1.1) with { CanSeek = false });
        queue.Dequeue()();
        Assert.False(service.CanSeek);
    }

    [Fact]
    public void DisposalDropsQueuedWorkAndImmediateFollow()
    {
        var queue = new Queue<Action>();
        var service = new LyricsPresentationService(queue.Enqueue);
        var track = new Track { Id = Guid.NewGuid() };
        service.SubmitSnapshot(Snapshot(1, track, 0));
        service.SubmitLyrics(new(1, track.Id, [new(TimeSpan.Zero, "ignored")], LyricsLoadingState.Ready));
        service.Dispose();
        queue.Dequeue()();
        service.SubmitSnapshot(Snapshot(2, track, 3));
        service.RequestImmediateFollow();
        Assert.Empty(queue);
        Assert.Empty(service.Rows);
        Assert.Equal(LyricsLoadingState.None, service.Status);
    }

    private static PlaybackSnapshot Snapshot(long session, Track track, double seconds) =>
        new(session, track, null, PlaybackState.Playing, TimeSpan.FromSeconds(seconds), TimeSpan.FromHours(2), true);
}
