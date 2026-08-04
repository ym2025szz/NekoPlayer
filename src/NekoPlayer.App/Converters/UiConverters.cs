using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;

namespace NekoPlayer.App.Converters;

public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is TimeSpan time ? TimeFormatter.Format(time) : "0:00";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BooleanInverseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime date ? RelativeTimeFormatter.Format(date, DateTime.UtcNow) : string.Empty;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FileAvailabilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var exists = value is true;
        return (parameter as string) switch
        {
            "Opacity" => exists ? 1d : 0.46d,
            "Text" => exists ? string.Empty : "文件不存在",
            _ => exists
        };
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SnackbarBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Brush.Parse(value switch
    {
        SnackbarTone.Error => "#9E3449",
        SnackbarTone.Warning => "#72591C",
        _ => "#176D69"
    });
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
