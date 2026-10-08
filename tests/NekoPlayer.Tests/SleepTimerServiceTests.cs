using System.Collections.Concurrent;
using NekoPlayer.App.Services;

namespace NekoPlayer.Tests;

public sealed class SleepTimerServiceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(240)]
    public void PresetsAndCustomMinutesUseTheRequestedDeadline(int minutes)
    {
        var clock = new ManualTimeProvider(); var context = new PumpContext();
        using var timer = new SleepTimerService(() => Task.CompletedTask, clock, context);
        timer.Start(minutes);
        Assert.True(timer.IsActive); Assert.Equal(TimeSpan.FromMinutes(minutes), timer.Remaining);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1), timer.Remaining);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(241)]
    public void InvalidCustomMinutesCannotActivateTheTimer(int minutes)
    {
        var clock = new ManualTimeProvider(); var context = new PumpContext();
        using var timer = new SleepTimerService(() => Task.CompletedTask, clock, context);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.Start(minutes));
        Assert.False(timer.IsActive); Assert.Equal(TimeSpan.Zero, timer.Remaining);
        Assert.Equal(0, context.PendingCount);
    }

    [Fact]
    public void CountdownNotificationsAreCoalescedAndDeliveredThroughTheUiContext()
    {
        var clock = new ManualTimeProvider(); var context = new PumpContext();
        using var timer = new SleepTimerService(() => Task.CompletedTask, clock, context);
        var changes = new List<string?>(); timer.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        timer.Start(15); clock.Advance(TimeSpan.FromSeconds(1)); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, context.PendingCount); Assert.Empty(changes);
        Assert.Equal("14:58", timer.RemainingText); Assert.Contains("14:58", timer.StatusText);
        context.Drain();
        Assert.Equal(1, changes.Count(x => x == nameof(SleepTimerService.RemainingText)));
        Assert.Contains(nameof(SleepTimerService.IsActive), changes);
        timer.Start(240);
        Assert.Equal("4:00:00", timer.RemainingText);
    }

    [Fact]
    public void SystemSleepUsesUtcDeadlineAndRepeatedCallbacksPauseOnlyOnce()
    {
        var clock = new ManualTimeProvider(); var context = new PumpContext(); var pauses = 0;
        using var timer = new SleepTimerService(() => { pauses++; return Task.CompletedTask; }, clock, context);
        timer.Start(30); context.Drain();
        clock.Advance(TimeSpan.FromHours(8));
        Parallel.For(0, 50, _ => clock.FireEvenIfStopped());
        Assert.False(timer.IsActive); Assert.Equal(TimeSpan.Zero, timer.Remaining);
        Assert.Equal(0, pauses); Assert.Equal(1, context.PendingCount);
        context.Drain(); clock.FireEvenIfStopped(); context.Drain();
        Assert.Equal(1, pauses); Assert.Equal("睡眠定时已结束", timer.StatusText);
    }

    [Fact]
    public void CancelConcurrentWithTheDeadlineInvalidatesAQueuedPause()
    {
        var clock = new ManualTimeProvider(); var context = new PumpContext(); var pauses = 0;
        using var timer = new SleepTimerService(() => { pauses++; return Task.CompletedTask; }, clock, context);
        timer.Start(1); context.Drain(); clock.Advance(TimeSpan.FromMinutes(1), fire: false);
        Parallel.Invoke(clock.FireEvenIfStopped, timer.Cancel);
        context.Drain(); clock.FireEvenIfStopped(); context.Drain();
        Assert.Equal(0, pauses); Assert.False(timer.IsActive);
        Assert.Equal("睡眠定时未开启", timer.StatusText);
    }

    [Fact]
    public void RestartReplacesTheDeadlineAndRejectsAnOlderQueuedExpiration()
    {
        var clock = new ManualTimeProvider(); var context = new PumpContext(); var pauses = 0;
        using var timer = new SleepTimerService(() => { pauses++; return Task.CompletedTask; }, clock, context);
        timer.Start(1); clock.Advance(TimeSpan.FromMinutes(1));
        timer.Start(90); context.Drain();
        Assert.Equal(0, pauses); Assert.True(timer.IsActive); Assert.Equal("1:30:00", timer.RemainingText);
        clock.Advance(TimeSpan.FromMinutes(89)); context.Drain(); Assert.Equal(0, pauses);
        clock.Advance(TimeSpan.FromMinutes(1)); context.Drain(); Assert.Equal(1, pauses);
    }

    [Fact]
    public void CancelBeforeTheDeadlineStopsBackgroundTicksWithoutPausing()
    {
        var clock = new ManualTimeProvider(); var context = new PumpContext(); var pauses = 0;
        using var timer = new SleepTimerService(() => { pauses++; return Task.CompletedTask; }, clock, context);
        timer.Start(1); clock.Advance(TimeSpan.FromSeconds(45)); timer.Cancel(); context.Drain();
        clock.Advance(TimeSpan.FromHours(1)); clock.FireEvenIfStopped(); context.Drain();
        Assert.Equal(0, pauses); Assert.False(timer.IsActive); Assert.Equal("00:00", timer.RemainingText);
    }

    [Fact]
    public void DisposeRejectsAnExpirationAlreadyQueuedToTheUi()
    {
        var clock = new ManualTimeProvider(); var context = new PumpContext(); var pauses = 0; var changes = 0;
        var timer = new SleepTimerService(() => { pauses++; return Task.CompletedTask; }, clock, context);
        timer.Start(1); context.Drain(); timer.PropertyChanged += (_, _) => changes++;
        clock.Advance(TimeSpan.FromMinutes(1)); timer.Dispose();
        context.Drain(); clock.FireEvenIfStopped(); context.Drain();
        Assert.Equal(0, pauses); Assert.Equal(0, changes); Assert.False(timer.IsActive);
        Assert.Throws<ObjectDisposedException>(() => timer.Start(15));
    }

    [Fact]
    public async Task DisposeAsyncWaitsForAPauseThatHasAlreadyStarted()
    {
        var clock = new ManualTimeProvider(); var context = new PumpContext();
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new SleepTimerService(() => pause.Task, clock, context);
        timer.Start(1); clock.Advance(TimeSpan.FromMinutes(1)); context.Drain();
        var dispose = timer.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        pause.SetResult(); await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(timer.IsActive);
    }

    [Fact]
    public void AFailedPauseIsObservedAndDoesNotRetry()
    {
        var clock = new ManualTimeProvider(); var context = new PumpContext(); var pauses = 0;
        using var timer = new SleepTimerService(() => { pauses++; throw new InvalidOperationException("pause failed"); }, clock, context);
        timer.Start(1); clock.Advance(TimeSpan.FromMinutes(1)); context.Drain();
        Assert.Equal(1, pauses); Assert.Equal("pause failed", timer.LastError);
        Assert.Contains("pause failed", timer.StatusText);
        clock.FireEvenIfStopped(); context.Drain(); Assert.Equal(1, pauses);
    }

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<Action> _callbacks = new();
        public int PendingCount => _callbacks.Count;
        public override void Post(SendOrPostCallback d, object? state) => _callbacks.Enqueue(() => d(state));
        public void Drain() { while (_callbacks.TryDequeue(out var callback)) callback(); }
    }

    // Advancing UTC without callbacks models Windows sleeping; one callback on resume must expire the original deadline.
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _utcNow = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        private ManualTimer? _timer;
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _utcNow; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new ManualTimer(callback, state);
            _timer.Change(dueTime, period);
            return _timer;
        }
        public void Advance(TimeSpan delta, bool fire = true)
        {
            lock (_sync) _utcNow += delta;
            if (fire && _timer?.Enabled == true) _timer.Fire();
        }
        public void FireEvenIfStopped() => _timer?.Fire();

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _enabled;
            public bool Enabled => Volatile.Read(ref _enabled) != 0;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { Volatile.Write(ref _enabled, dueTime == Timeout.InfiniteTimeSpan ? 0 : 1); return true; }
            public void Fire() => callback(state);
            public void Dispose() => Volatile.Write(ref _enabled, 0);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
