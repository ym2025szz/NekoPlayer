using System.Globalization;
using Avalonia.Data.Converters;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;

namespace NekoPlayer.App.Converters;

/// <summary>Keep the action and the exact song/source available to keyboard and screen-reader users.</summary>
public sealed class TrackActionNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Track track
            ? $"{parameter ?? "歌曲操作"}：{track.Title}，{track.Artist}，{track.ProviderName}"
            : parameter?.ToString() ?? "歌曲操作";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class TrackDetailsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Track track
            ? $"{track.Title}\n歌手：{track.Artist}\n专辑：{track.Album}\n版本：{(string.IsNullOrWhiteSpace(track.VersionLabel) ? "来源未提供" : track.VersionLabel)}\n来源：{track.ProviderName} · {TimeFormatter.Format(track.Duration)}\n{track.AvailabilityText}"
            : string.Empty;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NonEmptyTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
