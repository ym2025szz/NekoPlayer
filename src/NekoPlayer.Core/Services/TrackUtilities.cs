using NekoPlayer.Core.Models;
using System.Text;

namespace NekoPlayer.Core.Services;

public static class AudioFileExtensions
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".ape" };

    public static bool IsSupported(string path) => Supported.Contains(Path.GetExtension(path));
    public static IReadOnlyCollection<string> All => Supported;
}

public static class WindowsPath
{
    public static string NormalizeFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("文件路径不能为空。", nameof(path));
        return Path.GetFullPath(path.Trim().Trim('"'));
    }

    public static string NormalizeDirectory(string path)
    {
        var fullPath = NormalizeFile(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public static class ImportSummary
{
    public static string Create(ImportResult result, bool libraryConfirmed, bool hiddenBySearch)
    {
        if (result.IsCancelled) return $"导入已取消，已完成 {result.ProcessedCount} 首";
        if (!libraryConfirmed) return "歌曲已写入数据库，但本地音乐列表刷新未确认，请重新加载或查看日志";

        var suffix = hiddenBySearch ? "；歌曲已导入，但当前搜索条件将其隐藏。" : string.Empty;
        if (result.ImportedCount > 0 && result.FailedCount == 0)
        {
            var updated = result.UpdatedCount > 0 ? $"，更新 {result.UpdatedCount} 首" : string.Empty;
            return $"已导入 {result.ImportedCount} 首歌曲{updated}{suffix}";
        }
        if ((result.ImportedCount > 0 || result.UpdatedCount > 0) && result.FailedCount > 0)
            return $"已导入 {result.ImportedCount} 首，更新 {result.UpdatedCount} 首，失败 {result.FailedCount} 首{suffix}";
        if (result.ImportedCount == 0 && result.UpdatedCount > 0 && result.FailedCount == 0)
            return $"已更新 {result.UpdatedCount} 首歌曲{suffix}";
        if (result.ImportedCount == 0 && result.UpdatedCount == 0 && result.SkippedCount > 0 && result.FailedCount == 0)
            return $"没有新增歌曲，{result.SkippedCount} 首已存在";
        if (result.ImportedCount == 0 && result.UpdatedCount == 0 && result.FailedCount > 0)
            return "导入失败，请查看详细信息或日志";
        return "没有找到可导入的音乐文件";
    }
}

public static class MetadataFallback
{
    public static string Title(string? title, string path) => string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title.Trim();
    public static string Artist(string? artist) => string.IsNullOrWhiteSpace(artist) ? "未知艺术家" : artist.Trim();
    public static string Album(string? album) => string.IsNullOrWhiteSpace(album) ? "未知专辑" : album.Trim();
}

public static class TrackSearch
{
    public static string NormalizeQuery(string? query) => string.IsNullOrWhiteSpace(query)
        ? string.Empty
        : string.Join(" ", query.Normalize(NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static bool Matches(Track track, string? query)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length == 0) return true;
        var fields = new[] { NormalizeQuery(track.Title), NormalizeQuery(track.Artist), NormalizeQuery(track.Album) };
        return normalized.Split(' ').All(term => fields.Any(field => field.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    public static int RelevanceScore(Track track, string? query)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length == 0) return 0;
        if (!Matches(track, normalized)) return -1;
        var title = NormalizeQuery(track.Title);
        var artist = NormalizeQuery(track.Artist);
        var album = NormalizeQuery(track.Album);
        var score = title.Equals(normalized, StringComparison.OrdinalIgnoreCase) ? 1000
            : title.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ? 500
            : title.Contains(normalized, StringComparison.OrdinalIgnoreCase) ? 250 : 0;
        foreach (var term in normalized.Split(' '))
        {
            if (title.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (title.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score += 60;
            else if (title.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 40;
            if (artist.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 30;
            else if (artist.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 20;
            if (album.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 10;
        }
        return score;
    }
}

public static class TimeFormatter
{
    public static string Format(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
}
