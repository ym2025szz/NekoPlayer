using System.Collections.ObjectModel;
using NekoPlayer.App.Services;
using NekoPlayer.App.ViewModels;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Tests;

public sealed class QueuePresentationTests
{
    [Fact]
    public void ProgressAndCurrentHighlightUpdatesKeepRowsAndProduceNoCollectionReset()
    {
        Track[] tracks = [new() { Title = "A" }, new() { Title = "B" }];
        ObservableCollection<QueueItemViewModel> rows = [];
        QueuePresentation.Synchronize(rows, tracks, tracks[0].Id);
        var original = rows.ToArray(); var changes = 0;
        rows.CollectionChanged += (_, _) => changes++;
        for (var i = 0; i < 100; i++) QueuePresentation.Synchronize(rows, tracks, tracks[i % 2].Id);
        Assert.Equal(0, changes);
        Assert.Same(original[0], rows[0]); Assert.Same(original[1], rows[1]);
        Assert.False(rows[0].IsCurrent); Assert.True(rows[1].IsCurrent);
    }

    [Fact]
    public void ReorderInsertAndRemoveKeepSurvivingRowsAndTheirIndices()
    {
        var a = new Track(); var b = new Track(); var c = new Track();
        ObservableCollection<QueueItemViewModel> rows = [];
        QueuePresentation.Synchronize(rows, [a, b], a.Id);
        var rowA = rows[0]; var rowB = rows[1];
        QueuePresentation.Synchronize(rows, [c, b, a], a.Id);
        Assert.Same(rowB, rows[1]); Assert.Same(rowA, rows[2]);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(x => x.DisplayIndex));
        QueuePresentation.Synchronize(rows, [b, a], b.Id);
        Assert.Same(rowB, rows[0]); Assert.Same(rowA, rows[1]);
        Assert.True(rowB.IsCurrent); Assert.False(rowA.IsCurrent);
    }

    [Fact]
    public void CatalogInstanceReplacementKeepsRowAndUpdatesActionAndPermissionBindings()
    {
        var original = new Track { Title = "Old" };
        ObservableCollection<QueueItemViewModel> rows = [];
        QueuePresentation.Synchronize(rows, [original], original.Id);
        var row = rows[0]; var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var refreshed = new Track { Id = original.Id, Title = "New", SourceKind = TrackSourceKind.Online, Availability = MusicAvailability.Preview };
        QueuePresentation.Synchronize(rows, [refreshed], refreshed.Id);
        Assert.Same(row, rows[0]); Assert.Same(refreshed, row.Track);
        Assert.Equal("立即播放：New", row.PlayAutomationName);
        Assert.True(row.Track.CanPreview);
        Assert.Contains(nameof(QueueItemViewModel.Track), changes); Assert.Contains(nameof(QueueItemViewModel.PlayAutomationName), changes);
    }
}
