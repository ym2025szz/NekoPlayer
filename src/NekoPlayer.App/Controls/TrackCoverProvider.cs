using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NekoPlayer.Infrastructure.Configuration;
using NekoPlayer.Infrastructure.Online;
using Serilog;

namespace NekoPlayer.App.Controls;

public sealed record TrackCoverResult(Bitmap? Image, bool IsDefault);

public sealed class TrackCoverProvider
{
    public const string DefaultCoverUri = "avares://NekoPlayer/Assets/AppIcon.png";

    private static readonly SemaphoreSlim DecodeSlots = new(2, 2);
    private static readonly Lazy<OnlineAssetCache> SharedOnlineAssets = new(() =>
        new OnlineAssetCache(Path.Combine(new UserDataPaths().Root, "Cache", "OnlineCovers")));
    private readonly Func<Stream> _openDefaultCover;
    private readonly Func<Stream, int, Bitmap> _decodeCover;
    private readonly OnlineAssetCache? _onlineAssets;
    private readonly Lazy<Bitmap?> _defaultCover;
    private int _defaultDecodeCount;

    public TrackCoverProvider()
        : this(() => AssetLoader.Open(new Uri(DefaultCoverUri)))
    {
    }

    public TrackCoverProvider(Func<Stream> openDefaultCover, OnlineAssetCache? onlineAssets = null,
        Func<Stream, int, Bitmap>? decodeCover = null)
    {
        _openDefaultCover = openDefaultCover;
        _onlineAssets = onlineAssets;
        _decodeCover = decodeCover ?? ((stream, width) => Bitmap.DecodeToWidth(stream, width));
        _defaultCover = new Lazy<Bitmap?>(LoadDefaultCover, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static TrackCoverProvider Shared { get; } = new();

    public Bitmap? DefaultCover => _defaultCover.Value;

    public int DefaultDecodeCount => Volatile.Read(ref _defaultDecodeCount);

    public Task<TrackCoverResult> GetCoverAsync(string? coverPath, int decodeWidth, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(coverPath))
            return Task.FromResult(DefaultResult());

        if (string.Equals(coverPath, DefaultCoverUri, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(DefaultResult());

        return LoadRealCoverAsync(coverPath, Math.Clamp(decodeWidth, 32, 1024), cancellationToken);
    }

    private async Task<TrackCoverResult> LoadRealCoverAsync(string coverPath, int decodeWidth, CancellationToken cancellationToken)
    {
        Uri? onlineUri = null;
        try
        {
            Stream stream;
            if (Uri.TryCreate(coverPath, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                onlineUri = uri;
                stream = await OnlineAssets.OpenCoverAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            else if (uri is not null && uri.Scheme == "avares")
            {
                stream = AssetLoader.Open(uri);
            }
            else
            {
                var path = uri?.IsFile == true ? uri.LocalPath : coverPath;
                if (!File.Exists(path))
                {
                    Log.Warning("歌曲封面缓存不存在，使用默认封面：{CoverPath}", SafeCoverSource(path));
                    return DefaultResult();
                }
                stream = File.OpenRead(path);
            }

            await using (stream.ConfigureAwait(false))
            {
                await DecodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var image = await Task.Run(() =>
                    {
                        Bitmap? bitmap = null;
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            bitmap = _decodeCover(stream, decodeWidth);
                            cancellationToken.ThrowIfCancellationRequested();
                            return bitmap;
                        }
                        catch
                        {
                            // Decode itself cannot be interrupted; dispose its result if cancellation won the race.
                            bitmap?.Dispose();
                            throw;
                        }
                    }, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        image.Dispose();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    return new TrackCoverResult(image, false);
                }
                finally { DecodeSlots.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (onlineUri is not null)
            {
                try { OnlineAssets.Invalidate(onlineUri); }
                catch (Exception invalidationError) when (invalidationError is IOException or UnauthorizedAccessException) { }
            }
            if (onlineUri is not null)
            {
                // Exception messages can also contain a signed request URI; log types rather than the exception tree.
                Log.Warning("歌曲封面读取或解码失败，使用默认封面：{CoverHost}（{ErrorType}，{InnerErrorType}）",
                    onlineUri.Host, ex.GetType().Name, ex.InnerException?.GetType().Name);
            }
            else
                Log.Warning(ex, "歌曲封面读取或解码失败，使用默认封面：{CoverPath}", SafeCoverSource(coverPath));
            return DefaultResult();
        }
    }

    private TrackCoverResult DefaultResult() => new(DefaultCover, true);
    private OnlineAssetCache OnlineAssets => _onlineAssets ?? SharedOnlineAssets.Value;

    private static string SafeCoverSource(string coverPath)
    {
        if (Uri.TryCreate(coverPath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.Host;
        if (coverPath.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            coverPath.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            return "无效的在线封面地址";
        return coverPath;
    }

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
