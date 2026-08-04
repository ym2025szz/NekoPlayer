using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Core.Services;

public sealed partial class LrcParser : ILyricsService
{
    public IReadOnlyList<LyricsLine> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        var offset = 0;
        var lines = new List<LyricsLine>();

        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var offsetMatch = OffsetRegex().Match(rawLine);
            if (offsetMatch.Success && int.TryParse(offsetMatch.Groups[1].Value, out var parsedOffset))
            {
                offset = parsedOffset;
                continue;
            }

            var matches = TimestampRegex().Matches(rawLine);
            if (matches.Count == 0) continue;
            var text = TimestampRegex().Replace(rawLine, string.Empty).Trim();
            foreach (Match match in matches)
            {
                if (!int.TryParse(match.Groups[1].Value, out var minutes) ||
                    !double.TryParse(match.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)) continue;
                var timestamp = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(offset);
                lines.Add(new LyricsLine(timestamp < TimeSpan.Zero ? TimeSpan.Zero : timestamp, text));
            }
        }

        return lines.OrderBy(x => x.Timestamp).ToArray();
    }

    public async Task<IReadOnlyList<LyricsLine>> LoadForTrackAsync(Track track, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(track.FilePath);
        if (directory is null) return [];
        var candidates = new[]
        {
            Path.ChangeExtension(track.FilePath, ".lrc"),
            Path.Combine(directory, $"{track.Title}.lrc")
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return [];
        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            return Parse(content);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var content = await File.ReadAllTextAsync(path, Encoding.GetEncoding(936), cancellationToken);
            return Parse(content);
        }
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:\.\d{1,3})?)\]", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex OffsetRegex();
}
