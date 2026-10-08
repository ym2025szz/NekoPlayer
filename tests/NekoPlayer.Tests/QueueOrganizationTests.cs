using System.Collections.ObjectModel;
using System.Collections.Specialized;
using NekoPlayer.App.Services;
using NekoPlayer.App.ViewModels;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;

namespace NekoPlayer.Tests;

public sealed class QueueOrganizationTests
{
    [Fact]
    public void MovingItemsAcrossCurrentKeepsTheSelectedTrackAndFutureSequence()
    {
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C"); var d = NewTrack("D");
        var queue = new PlaybackQueueService(); queue.Replace([a, b, c, d], c.Id);
        var queueEvents = 0; var currentEvents = 0;
        queue.QueueChanged += (_, _) => queueEvents++;
        queue.CurrentChanged += (_, _) => currentEvents++;

        queue.Move(a.Id, 3);
        Assert.Equal(new[] { b.Id, c.Id, d.Id, a.Id }, queue.Items.Select(x => x.Id));
        Assert.Same(c, queue.Current); Assert.Equal(1, queue.CurrentIndex);
        queue.Move(d.Id, 0);
        Assert.Equal(new[] { d.Id, b.Id, c.Id, a.Id }, queue.Items.Select(x => x.Id));
        Assert.Same(c, queue.Current); Assert.Equal(2, queue.CurrentIndex);
        queue.Move(c.Id, 0);
        Assert.Same(c, queue.Current); Assert.Equal(0, queue.CurrentIndex);
        Assert.Equal(3, queueEvents); Assert.Equal(0, currentEvents);
        Assert.Same(d, queue.MoveNext());
    }

    [Fact]
    public void MoveClampsEndpointsAndOnlyReportsChanges()
    {
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C");
        var queue = new PlaybackQueueService(); queue.Replace([a, b, c], b.Id);
        var changes = 0; queue.QueueChanged += (_, _) => changes++;
        queue.Move(Guid.NewGuid(), 0); queue.Move(b.Id, 1);
        Assert.Equal(0, changes);
        queue.Move(a.Id, int.MaxValue); queue.Move(a.Id, int.MaxValue);
        queue.Move(a.Id, int.MinValue); queue.Move(a.Id, int.MinValue);
        Assert.Equal(2, changes);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, queue.Items.Select(x => x.Id));
        Assert.Same(b, queue.Current);
    }

    [Fact]
    public void ClearPendingKeepsCurrentIdentityAndDoesNotPublishAPlaybackSelection()
    {
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C");
        var queue = new PlaybackQueueService(); queue.Replace([a, b, c], b.Id);
        var changes = 0; var selections = 0;
        queue.QueueChanged += (_, _) => changes++;
        queue.CurrentChanged += (_, _) => selections++;
        queue.ClearPending(); queue.ClearPending();
        Assert.Same(b, Assert.Single(queue.Items));
        Assert.Same(b, queue.Current); Assert.Equal(0, queue.CurrentIndex);
        Assert.Equal(1, changes); Assert.Equal(0, selections);
    }

    [Fact]
    public void OrganizationPreservesAnExplicitlyUnselectedQueue()
    {
        var a = NewTrack("A"); var b = NewTrack("B");
        var queue = new PlaybackQueueService(); queue.Restore([a, b], -1);
        queue.Move(b.Id, 0);
        Assert.Null(queue.Current); Assert.Equal(-1, queue.CurrentIndex);
        var changes = 0; var selections = 0;
        queue.QueueChanged += (_, _) => changes++;
        queue.CurrentChanged += (_, _) => selections++;
        queue.ClearPending(); queue.ClearPending();
        Assert.Empty(queue.Items); Assert.Null(queue.Current); Assert.Equal(-1, queue.CurrentIndex);
        Assert.Equal(1, changes); Assert.Equal(0, selections);
    }

    [Fact]
    public void RepeatedConcurrentMovesNeverDuplicateItemsOrLoseTheCurrentAnchor()
    {
        var tracks = Enumerable.Range(0, 10).Select(i => NewTrack(i.ToString())).ToArray();
        var current = tracks[5]; var queue = new PlaybackQueueService(); queue.Replace(tracks, current.Id);
        Parallel.For(0, 300, i => queue.Move(tracks[i % tracks.Length].Id, i % tracks.Length));
        Assert.Equal(tracks.Length, queue.Items.Count);
        Assert.Equal(tracks.Length, queue.Items.Select(x => x.Id).Distinct().Count());
        Assert.Same(current, queue.Current);
        Assert.Equal(current.Id, queue.Items[queue.CurrentIndex].Id);
    }

    [Fact]
    public void QueueOrganizationPreservesVisibleRowsWithoutCollectionReset()
    {
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C");
        var queue = new PlaybackQueueService(); queue.Replace([a, b, c], b.Id);
        ObservableCollection<QueueItemViewModel> rows = [];
        QueuePresentation.Synchronize(rows, queue.Items, queue.Current?.Id);
        var rowA = rows[0]; var rowB = rows[1]; var rowC = rows[2];
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => changes.Add(e.Action);
        queue.QueueChanged += (_, _) => QueuePresentation.Synchronize(rows, queue.Items, queue.Current?.Id);
        queue.Move(a.Id, 2);
        Assert.Same(rowB, rows[0]); Assert.Same(rowC, rows[1]); Assert.Same(rowA, rows[2]);
        Assert.True(rowB.IsCurrent);
        queue.ClearPending();
        Assert.Same(rowB, Assert.Single(rows)); Assert.True(rowB.IsCurrent);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    }

    private static Track NewTrack(string title) => new() { Title = title };
}
