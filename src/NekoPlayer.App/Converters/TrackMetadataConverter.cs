using System.Globalization;
using Avalonia.Data.Converters;
using NekoPlayer.Core.Models;

namespace NekoPlayer.App.Converters;

public sealed class TrackMetadataConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Track track
            ? string.Join(" · ", new[] { track.ProviderName, track.Album, track.VersionLabel }
                .Where(text => !string.IsNullOrWhiteSpace(text)))
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
