using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;

namespace NekoPlayer.Tests;

public sealed class PlaybackQueueTests
{
    [Fact]
    public void SequentialStopsAtEnd()
    {
        var queue = CreateQueue(2); queue.PlayMode = PlayMode.Sequential; queue.SetCurrent(queue.Items[1].Id);
        Assert.Null(queue.MoveNext(true));
    }

    [Fact]
    public void RepeatAllWrapsToFirst()
    {
        var queue = CreateQueue(2); queue.PlayMode = PlayMode.RepeatAll; queue.SetCurrent(queue.Items[1].Id);
        Assert.Equal(queue.Items[0].Id, queue.MoveNext(true)!.Id);
    }

    [Fact]
    public void RepeatOneKeepsCurrentAfterCompletion()
    {
        var queue = CreateQueue(3); queue.PlayMode = PlayMode.RepeatOne; var current = queue.Items[1]; queue.SetCurrent(current.Id);
        Assert.Equal(current.Id, queue.MoveNext(true)!.Id);
    }

    [Fact]
    public void ShuffleAvoidsImmediateRepeat()
    {
        var queue = CreateQueue(3); queue.PlayMode = PlayMode.Shuffle; var current = queue.Current;
        Assert.NotEqual(current!.Id, queue.MoveNext(true)!.Id);
    }

    [Fact]
    public void PreviousAfterThreeSecondsRestartsCurrent()
    {
        var queue = CreateQueue(3); queue.SetCurrent(queue.Items[1].Id);
        Assert.Equal(queue.Items[1].Id, queue.MovePrevious(TimeSpan.FromSeconds(3.01))!.Id);
        Assert.Equal(queue.Items[0].Id, queue.MovePrevious(TimeSpan.FromSeconds(3))!.Id);
    }

    [Fact]
    public void QueueDeduplicatesAndSupportsPlayNext()
    {
        var queue = CreateQueue(2); var added = NewTrack("C"); queue.Add(added); queue.Add(added); queue.PlayNext(added);
        Assert.Equal(3, queue.Items.Count);
        Assert.Equal(added.Id, queue.Items[queue.CurrentIndex + 1].Id);
    }

    private static PlaybackQueueService CreateQueue(int count)
    {
        var service = new PlaybackQueueService(); service.Replace(Enumerable.Range(0, count).Select(i => NewTrack(i.ToString()))); return service;
    }
    private static Track NewTrack(string title) => new() { Id = Guid.NewGuid(), Title = title, FilePath = title + ".mp3" };
}
