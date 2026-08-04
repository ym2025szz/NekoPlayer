using System.Security.Cryptography;
using System.Text;
using FFMpegCore;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using Serilog;
using TagLibFile = TagLib.File;

namespace NekoPlayer.Infrastructure.Metadata;

public sealed class TrackMetadataReader(IFfmpegLocator ffmpeg, IUserDataPaths paths)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    public Task<Track> ReadAsync(string filePath, CancellationToken cancellationToken) =>
        ReadAsync(filePath, null, cancellationToken);

    public async Task<Track> ReadAsync(string filePath, Action<ImportStage>? stageChanged, CancellationToken cancellationToken)
    {
        var info = new FileInfo(filePath);
        stageChanged?.Invoke(ImportStage.ReadingMetadata);
        var tags = await Task.Run(() => ReadTags(filePath, info.LastWriteTimeUtc), cancellationToken).ConfigureAwait(false);

        var duration = TimeSpan.Zero;
        var codec = string.Empty;
        long bitRate = 0;
        var sampleRate = 0;
        var channels = 0;
        if (ffmpeg.IsAvailable)
        {
            stageChanged?.Invoke(ImportStage.AnalyzingMedia);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                var analysis = await FFProbe.AnalyseAsync(filePath, cancellationToken: timeout.Token).ConfigureAwait(false);
                duration = analysis.Duration;
                var audio = analysis.PrimaryAudioStream;
                if (audio is not null)
                {
                    codec = audio.CodecName ?? string.Empty;
                    bitRate = audio.BitRate;
                    sampleRate = audio.SampleRateHz;
                    channels = audio.Channels;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"媒体分析超过 {ProbeTimeout.TotalSeconds:0} 秒：{Path.GetFileName(filePath)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "FFprobe 无法分析文件，使用可恢复的元数据结果：{FilePath}", filePath);
            }
        }

        return new Track
        {
            FilePath = info.FullName,
            Title = MetadataFallback.Title(tags.Title, filePath),
            Artist = MetadataFallback.Artist(tags.Artist),
            Album = MetadataFallback.Album(tags.Album),
            Genre = tags.Genre?.Trim() ?? string.Empty,
            Year = (int)tags.Year,
            TrackNumber = (int)tags.TrackNumber,
            Duration = duration,
            CodecName = codec,
            BitRate = bitRate,
            SampleRate = sampleRate,
            Channels = channels,
            CoverCachePath = tags.CoverPath,
            FileSize = info.Length,
            FileLastWriteTimeUtc = info.LastWriteTimeUtc,
            AddedAt = DateTime.UtcNow
        };
    }

    private TagReadResult ReadTags(string filePath, DateTime modified)
    {
        try
        {
            using var tagFile = TagLibFile.Create(filePath);
            return new TagReadResult(
                tagFile.Tag.Title,
                tagFile.Tag.FirstPerformer,
                tagFile.Tag.Album,
                tagFile.Tag.FirstGenre,
                tagFile.Tag.Year,
                tagFile.Tag.Track,
                CacheCover(tagFile, filePath, modified));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TagLib 无法解析文件，使用文件名回退：{FilePath}", filePath);
            return new TagReadResult(null, null, null, null, 0, 0, null);
        }
    }

    private string? CacheCover(TagLibFile file, string sourcePath, DateTime modified)
    {
        var picture = file.Tag.Pictures.FirstOrDefault();
        if (picture?.Data.Data is not { Length: > 0 } data || data.Length > 10 * 1024 * 1024) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath + modified.Ticks)))[..24];
        var extension = picture.MimeType?.Contains("png", StringComparison.OrdinalIgnoreCase) == true ? ".png" : ".jpg";
        var target = Path.Combine(paths.CoversDirectory, hash + extension);
        if (!File.Exists(target)) File.WriteAllBytes(target, data);
        return target;
    }

    private sealed record TagReadResult(
        string? Title,
        string? Artist,
        string? Album,
        string? Genre,
        uint Year,
        uint TrackNumber,
        string? CoverPath);
}
