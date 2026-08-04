using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;

namespace NekoPlayer.App.Controls;

public sealed record TrackCoverResult(Bitmap? Image, bool IsDefault);

public sealed class TrackCoverProvider
{
    public const string DefaultCoverUri = "avares://NekoPlayer/Assets/AppIcon.png";

    private readonly Func<Stream> _openDefaultCover;
    private readonly Lazy<Bitmap?> _defaultCover;
    private int _defaultDecodeCount;

    public TrackCoverProvider()
        : this(() => AssetLoader.Open(new Uri(DefaultCoverUri)))
    {
    }

    public TrackCoverProvider(Func<Stream> openDefaultCover)
    {
        _openDefaultCover = openDefaultCover;
        _defaultCover = new Lazy<Bitmap?>(LoadDefaultCover, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static TrackCoverProvider Shared { get; } = new();

    public Bitmap? DefaultCover => _defaultCover.Value;

    public int DefaultDecodeCount => Volatile.Read(ref _defaultDecodeCount);

    public Task<TrackCoverResult> GetCoverAsync(string? coverPath, int decodeWidth, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(coverPath))
            return Task.FromResult(DefaultResult());

        return LoadRealCoverAsync(coverPath, Math.Clamp(decodeWidth, 32, 1024), cancellationToken);
    }

    private async Task<TrackCoverResult> LoadRealCoverAsync(string coverPath, int decodeWidth, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(coverPath))
            {
                Log.Warning("歌曲封面缓存不存在，使用默认封面：{CoverPath}", coverPath);
                return DefaultResult();
            }

            var image = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(coverPath);
                var bitmap = Bitmap.DecodeToWidth(stream, decodeWidth);
                cancellationToken.ThrowIfCancellationRequested();
                return bitmap;
            }, cancellationToken).ConfigureAwait(false);

            return new TrackCoverResult(image, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "歌曲封面读取或解码失败，使用默认封面：{CoverPath}", coverPath);
            return DefaultResult();
        }
    }

    private TrackCoverResult DefaultResult() => new(DefaultCover, true);

    private Bitmap? LoadDefaultCover()
    {
        try
        {
            Interlocked.Increment(ref _defaultDecodeCount);
            using var stream = _openDefaultCover();
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "默认歌曲封面资源加载失败：{DefaultCoverUri}", DefaultCoverUri);
            return null;
        }
    }
}
