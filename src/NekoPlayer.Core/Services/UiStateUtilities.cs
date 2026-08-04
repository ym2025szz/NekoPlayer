using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Core.Services;

public static class ResponsiveLayout
{
    public const double WideBreakpoint = 1200;
    public const double CompactBreakpoint = 1000;

    public static ResponsiveLayoutMode Resolve(double width) => width >= WideBreakpoint
        ? ResponsiveLayoutMode.Wide
        : width >= CompactBreakpoint
            ? ResponsiveLayoutMode.Standard
            : ResponsiveLayoutMode.Compact;
}

public static class PlaybackActionMapper
{
    public static PlaybackPrimaryAction Resolve(PlaybackState state, bool hasCurrentTrack, bool hasQueue) => state switch
    {
        PlaybackState.Playing => PlaybackPrimaryAction.Pause,
        PlaybackState.Loading or PlaybackState.Seeking or PlaybackState.Buffering => PlaybackPrimaryAction.Busy,
        PlaybackState.Error => hasCurrentTrack || hasQueue ? PlaybackPrimaryAction.Retry : PlaybackPrimaryAction.Disabled,
        _ => hasCurrentTrack || hasQueue ? PlaybackPrimaryAction.Play : PlaybackPrimaryAction.Disabled
    };

    public static string Tooltip(PlaybackPrimaryAction action) => action switch
    {
        PlaybackPrimaryAction.Pause => "暂停",
        PlaybackPrimaryAction.Play => "播放",
        PlaybackPrimaryAction.Busy => "正在处理",
        PlaybackPrimaryAction.Retry => "重试播放",
        _ => "暂无可播放歌曲"
    };
}

public static class SeekTarget
{
    public static TimeSpan Clamp(TimeSpan target, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return TimeSpan.Zero;
        var safeEnd = duration > TimeSpan.FromMilliseconds(500)
            ? duration - TimeSpan.FromMilliseconds(250)
            : duration;
        if (target < TimeSpan.Zero) return TimeSpan.Zero;
        return target > safeEnd ? safeEnd : target;
    }
}

public static class SeekPointerMath
{
    public const double DefaultEdgePadding = 8;

    public static double ValueFromX(
        double x,
        double width,
        double minimum,
        double maximum,
        double edgePadding = DefaultEdgePadding)
    {
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(minimum) ||
            !double.IsFinite(maximum) || maximum <= minimum) return minimum;

        var padding = Math.Clamp(double.IsFinite(edgePadding) ? edgePadding : 0, 0, width / 2);
        var usableWidth = Math.Max(1, width - padding * 2);
        var ratio = Math.Clamp((x - padding) / usableWidth, 0, 1);
        return minimum + (maximum - minimum) * ratio;
    }
}

public static class QueueIndexing
{
    public static IReadOnlyList<QueueDisplaySnapshot> Create(IReadOnlyList<Track> tracks, Guid? currentTrackId) =>
        tracks.Select((track, index) => new QueueDisplaySnapshot(index + 1, track, track.Id == currentTrackId)).ToArray();
}

public static class GreetingFormatter
{
    public static string GetGreeting(DateTimeOffset localNow) => localNow.Hour switch
    {
        >= 5 and <= 10 => "早上好，欢迎回来",
        >= 11 and <= 12 => "中午好，记得休息一下",
        >= 13 and <= 17 => "下午好，来听点音乐吧",
        >= 18 and <= 22 => "晚上好，享受音乐时光",
        _ => "夜深了，注意音量哦"
    };
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset LocalNow => DateTimeOffset.Now;
}

public static class RelativeTimeFormatter
{
    public static string Format(DateTime utcValue, DateTime utcNow)
    {
        var value = utcValue.Kind == DateTimeKind.Utc ? utcValue : utcValue.ToUniversalTime();
        var delta = utcNow - value;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
        if (delta < TimeSpan.FromMinutes(1)) return "刚刚";
        if (delta < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)delta.TotalMinutes)} 分钟前";
        if (value.Date == utcNow.Date.AddDays(-1)) return "昨天";
        if (delta < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)delta.TotalHours)} 小时前";
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}

public sealed class UndoActionCoordinator
{
    private readonly object _sync = new();
    private UndoEntry? _current;

    public Guid? CurrentSubjectId
    {
        get { lock (_sync) return _current?.SubjectId; }
    }

    public Guid Set(Guid subjectId, Func<CancellationToken, Task> action)
    {
        var token = Guid.NewGuid();
        lock (_sync) _current = new UndoEntry(token, subjectId, action);
        return token;
    }

    public bool Clear(Guid token)
    {
        lock (_sync)
        {
            if (_current?.Token != token) return false;
            _current = null;
            return true;
        }
    }

    public void Reset()
    {
        lock (_sync) _current = null;
    }

    public async Task<bool> ExecuteCurrentAsync(CancellationToken cancellationToken = default)
    {
        UndoEntry? entry;
        lock (_sync)
        {
            entry = _current;
            _current = null;
        }
        if (entry is null) return false;
        await entry.Action(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private sealed record UndoEntry(Guid Token, Guid SubjectId, Func<CancellationToken, Task> Action);
}

public sealed class SeekRequestCoordinator : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _current;

    public async Task SubmitAsync(
        TimeSpan target,
        Func<TimeSpan, CancellationToken, Task> commit,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (_sync)
        {
            _current?.Cancel();
            _current?.Dispose();
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _current = linked;
        }

        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, linked.Token).ConfigureAwait(false);
            await commit(target, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_current, linked)) _current = null;
            }
            linked.Dispose();
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _current?.Cancel();
            _current?.Dispose();
            _current = null;
        }
    }

    public void Dispose() => Cancel();
}
