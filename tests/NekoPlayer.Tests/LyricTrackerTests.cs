using NekoPlayer.App.Services;

namespace NekoPlayer.Tests;

public sealed class LyricTrackerTests
{
    [Fact]
    public void FiveThousandLinesAdvanceAndSeekWithoutChangingUnrelatedRows()
    {
        var tracker = new LyricPositionTracker();
        tracker.Reset(Enumerable.Range(0, 5000).Select(i => TimeSpan.FromSeconds(i)));
        Assert.Equal(new LyricPositionChange(-1, 0), tracker.Update(TimeSpan.Zero));
        Assert.False(tracker.Update(TimeSpan.FromMilliseconds(150)).HasChanged);
        Assert.Equal(new LyricPositionChange(0, 1), tracker.Update(TimeSpan.FromSeconds(1)));
        Assert.Equal(new LyricPositionChange(1, 4999), tracker.Update(TimeSpan.FromSeconds(4999), isSeek: true));
        Assert.Equal(new LyricPositionChange(4999, 1234), tracker.Update(TimeSpan.FromSeconds(1234), isSeek: true));
        Assert.Equal(new LyricPositionChange(1234, 1235), tracker.Update(TimeSpan.FromSeconds(1235)));
        Assert.Equal(new LyricPositionChange(1235, -1), tracker.Update(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void DuplicateTimestampsSelectTheLastMatchingLineAndResetClearsIndex()
    {
        var tracker = new LyricPositionTracker();
        tracker.Reset([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]);
        Assert.Equal(1, tracker.Update(TimeSpan.FromSeconds(1)).CurrentIndex);
        Assert.Equal(2, tracker.Update(TimeSpan.FromSeconds(2)).CurrentIndex);
        tracker.Reset([]);
        Assert.Equal(-1, tracker.CurrentIndex);
        Assert.False(tracker.Update(TimeSpan.Zero).HasChanged);
    }

    [Fact]
    public void UnsortedTimestampsAreRejectedWithoutLosingPreviousState()
    {
        var tracker = new LyricPositionTracker();
        tracker.Reset([TimeSpan.Zero]);
        tracker.Update(TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => tracker.Reset([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1)]));
        Assert.Equal(0, tracker.CurrentIndex);
    }

    [Fact]
    public void DispatcherKeepsOneQueuedCallbackAndDeliversLatestSnapshot()
    {
        var queue = new Queue<Action>();
        float[]? delivered = null;
        using var dispatcher = new LatestSpectrumDispatcher(bands => delivered = bands.ToArray(), queue.Enqueue);
        var input = new float[40];
        for (var frame = 0; frame < 100; frame++)
        {
            Array.Fill(input, frame / 100f);
            dispatcher.Submit(input);
            Assert.Single(queue);
        }
        Array.Fill(input, 0); // Mutating the producer's buffer cannot change the snapshot already submitted.
        queue.Dequeue()();
        Assert.Empty(queue);
        Assert.Equal(40, delivered!.Length);
        Assert.All(delivered, value => Assert.Equal(0.99f, value));
    }

    [Fact]
    public void FramesSubmittedDuringDeliveryScheduleOnlyOneFollowupCallback()
    {
        var queue = new Queue<Action>();
        var frames = 0;
        LatestSpectrumDispatcher? dispatcher = null;
        dispatcher = new LatestSpectrumDispatcher(_ =>
        {
            frames++;
            if (frames != 1) return;
            for (var i = 0; i < 100; i++) dispatcher!.Submit([0.7f]);
            Assert.Empty(queue);
        }, queue.Enqueue);
        using (dispatcher)
        {
            dispatcher.Submit([0.1f]);
            queue.Dequeue()();
            Assert.Single(queue);
            queue.Dequeue()();
            Assert.Equal(2, frames);
            Assert.Empty(queue);
        }
    }

    [Fact]
    public void DisposedDispatcherDoesNotApplyPendingFrameAndBarsKeepTheirIdentity()
    {
        var queue = new Queue<Action>();
        var applied = false;
        var dispatcher = new LatestSpectrumDispatcher(_ => applied = true, queue.Enqueue);
        dispatcher.Submit([0.5f]);
        dispatcher.Dispose();
        queue.Dequeue()();
        dispatcher.Submit([1f]);
        Assert.False(applied);
        Assert.Empty(queue);
        var bar = new SpectrumBarViewModel();
        bar.SetAmplitude(0.5f);
        Assert.Equal(49, bar.Height);
        bar.SetAmplitude(float.NaN);
        Assert.Equal(3, bar.Height);
    }
}
