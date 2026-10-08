using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;

namespace NekoPlayer.App.Services;

/// <summary>A transient UTC deadline that pauses playback once and survives a hidden window or system sleep.</summary>
public sealed class SleepTimerService : INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<Task> _pauseAsync;
    private readonly TimeProvider _timeProvider;
    private readonly Action<Action> _post;
    private readonly ITimer _timer;
    private readonly HashSet<Task> _pendingPauses = [];
    private DateTimeOffset? _deadlineUtc;
    private TimeSpan _remaining;
    private long _generation;
    private long? _pendingExpiration;
    private bool _notificationPending;
    private bool _hasExpired;
    private bool _disposed;
    private string? _lastError;

    public SleepTimerService(Func<Task> pauseAsync, TimeProvider? timeProvider = null,
        SynchronizationContext? synchronizationContext = null)
    {
        ArgumentNullException.ThrowIfNull(pauseAsync);
        _pauseAsync = pauseAsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var context = synchronizationContext ?? SynchronizationContext.Current;
        _post = context is null
            ? action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background)
            : action => context.Post(_ => action(), null);
        _timer = _timeProvider.CreateTimer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsActive { get { lock (_sync) return _deadlineUtc.HasValue; } }
    public TimeSpan Remaining { get { lock (_sync) return _remaining; } }
    public string? LastError { get { lock (_sync) return _lastError; } }
    public string RemainingText
    {
        get
        {
            var remaining = Remaining;
            return remaining.TotalHours >= 1
                ? remaining.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : remaining.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }
    }
    public string StatusText
    {
        get
        {
            lock (_sync)
                return _deadlineUtc.HasValue ? $"将在 {RemainingText} 后暂停播放"
                    : _lastError is not null ? $"定时暂停失败：{_lastError}"
                    : _hasExpired ? "睡眠定时已结束" : "睡眠定时未开启";
        }
    }

    public void Start(int minutes)
    {
        if (minutes is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(minutes), "睡眠定时支持 1 到 240 分钟。");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _generation++;
            _pendingExpiration = null;
            _hasExpired = false;
            _lastError = null;
            _remaining = TimeSpan.FromMinutes(minutes);
            _deadlineUtc = _timeProvider.GetUtcNow() + _remaining;
            _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        QueueNotification();
    }

    public void Cancel()
    {
        lock (_sync)
        {
            if (_disposed || !_deadlineUtc.HasValue && !_pendingExpiration.HasValue && !_hasExpired && _lastError is null) return;
            _generation++;
            _deadlineUtc = null;
            _remaining = TimeSpan.Zero;
            _pendingExpiration = null;
            _hasExpired = false;
            _lastError = null;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        QueueNotification();
    }

    private void OnTick(object? state)
    {
        lock (_sync)
        {
            if (_disposed || _deadlineUtc is not { } deadline) return;
            var remaining = deadline - _timeProvider.GetUtcNow();
            // Round up so the last partial second still displays as 00:01.
            remaining = remaining <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
            if (_remaining == remaining && remaining > TimeSpan.Zero) return;
            _remaining = remaining;
            if (remaining == TimeSpan.Zero)
            {
                _deadlineUtc = null;
                _hasExpired = true;
                _pendingExpiration = _generation;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
        QueueNotification();
    }

    private void QueueNotification()
    {
        lock (_sync)
        {
            if (_disposed || _notificationPending) return;
            _notificationPending = true;
        }
        try { _post(DeliverLatest); }
        catch
        {
            lock (_sync) _notificationPending = false;
            throw;
        }
    }

    private void DeliverLatest()
    {
        lock (_sync)
        {
            _notificationPending = false;
            if (_disposed) return;
        }
        foreach (var name in new[] { nameof(IsActive), nameof(Remaining), nameof(RemainingText), nameof(StatusText), nameof(LastError) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        Task? pause = null;
        long generation = 0;
        lock (_sync)
        {
            // Cancel or Start can invalidate an expiration while its UI callback is queued.
            if (!_disposed && _pendingExpiration is { } pending && pending == _generation)
            {
                generation = pending;
                _pendingExpiration = null;
                try { pause = _pauseAsync(); }
                catch (Exception ex) { pause = Task.FromException(ex); }
                _pendingPauses.Add(pause);
            }
        }
        if (pause is not null) _ = ObservePauseAsync(pause, generation);
    }

    private async Task ObservePauseAsync(Task pause, long generation)
    {
        Exception? failure = null;
        try { await pause.ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        var notify = false;
        lock (_sync)
        {
            _pendingPauses.Remove(pause);
            if (!_disposed && generation == _generation && failure is not null)
            {
                _lastError = failure.Message;
                notify = true;
            }
        }
        if (notify) QueueNotification();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _deadlineUtc = null;
            _remaining = TimeSpan.Zero;
            _pendingExpiration = null;
            _timer.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task[] pending;
        lock (_sync) pending = _pendingPauses.ToArray();
        // Expiration failures are surfaced by LastError; disposing must still release the timer.
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { }
    }
}
