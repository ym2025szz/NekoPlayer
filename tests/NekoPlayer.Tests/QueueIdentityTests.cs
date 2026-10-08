using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;

namespace NekoPlayer.Tests;

public sealed class QueueIdentityTests
{
    [Fact]
    public void MovingExistingEarlierTrackNextKeepsCurrentIdentity()
    {
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C"); var d = NewTrack("D");
        var queue = new PlaybackQueueService();
        queue.Replace([a, b, c, d], c.Id);
        queue.PlayNext(a);
        Assert.Equal(c.Id, queue.Current!.Id);
        Assert.Equal(new[] { b.Id, c.Id, a.Id, d.Id }, queue.Items.Select(x => x.Id));
        Assert.Equal(a.Id, queue.Items[queue.CurrentIndex + 1].Id);
    }

    [Fact]
    public void MovingCurrentTrackNextIsStableNoOp()
    {
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C");
        var queue = new PlaybackQueueService();
        queue.Replace([a, b, c], b.Id);
        queue.PlayNext(b);
        Assert.Equal(b.Id, queue.Current!.Id);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, queue.Items.Select(x => x.Id));
    }

    [Fact]
    public void QueueSnapshotsDoNotChangeWhenListMutates()
    {
        var a = NewTrack("A"); var b = NewTrack("B");
        var queue = new PlaybackQueueService(); queue.Replace([a]);
        var before = queue.Items;
        queue.Add(b); queue.Remove(a.Id);
        Assert.Equal(a.Id, Assert.Single(before).Id);
        Assert.Equal(b.Id, Assert.Single(queue.Items).Id);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void RemovingDifferentItemPreservesCurrentAndRemovingCurrentClearsIt()
    {
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C");
        var queue = new PlaybackQueueService(); queue.Replace([a, b, c], b.Id);
        queue.Remove(a.Id);
        Assert.Equal(b.Id, queue.Current!.Id);
        queue.Remove(b.Id);
        Assert.Null(queue.Current);
        Assert.Equal(-1, queue.CurrentIndex);
        Assert.Equal(c.Id, Assert.Single(queue.Items).Id);
    }

    [Fact]
    public void RestorePreservesExplicitlyClearedSelection()
    {
        var queue = new PlaybackQueueService(); queue.Restore([NewTrack("A")], -1);
        Assert.Null(queue.Current);
        Assert.Single(queue.Items);
    }

    private static Track NewTrack(string title) => new() { Id = Guid.NewGuid(), Title = title };
}
