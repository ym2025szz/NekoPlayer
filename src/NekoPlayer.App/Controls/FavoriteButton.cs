using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HeartPath = Avalonia.Controls.Shapes.Path;

namespace NekoPlayer.App.Controls;

public sealed class FavoriteButton : ToggleButton
{
    public static readonly StyledProperty<string?> AutomationContextProperty =
        AvaloniaProperty.Register<FavoriteButton, string?>(nameof(AutomationContext));
    public string? AutomationContext { get => GetValue(AutomationContextProperty); set => SetValue(AutomationContextProperty, value); }
    public const string UncheckedColor = "#8FAEB2";
    public const string CheckedColor = "#FF5D7D";
    public const string OutlineGeometryData = "M 10,17.4 C 8.1,16 3.3,12.6 3.3,8.5 C 3.3,5.6 5.2,3.7 7.6,3.7 C 9,3.7 10,4.7 10,4.7 C 10,4.7 11,3.7 12.4,3.7 C 14.8,3.7 16.7,5.6 16.7,8.5 C 16.7,12.6 11.9,16 10,17.4 Z";
    public const string FilledGeometryData = "M 10,18 C 8,16.5 3,13 3,8.5 C 3,5.5 5,3.5 7.5,3.5 C 9,3.5 10,4.5 10,4.5 C 10,4.5 11,3.5 12.5,3.5 C 15,3.5 17,5.5 17,8.5 C 17,13 12,16.5 10,18 Z";
    public const string AddTooltip = "添加到喜欢";
    public const string RemoveTooltip = "取消喜欢";

    private static readonly IBrush UncheckedBrush = new ImmutableSolidColorBrush(Color.Parse(UncheckedColor));
    private static readonly IBrush UncheckedHoverBrush = new ImmutableSolidColorBrush(Color.Parse("#C4D7D9"));
    private static readonly IBrush CheckedBrush = new ImmutableSolidColorBrush(Color.Parse(CheckedColor));
    private static readonly IBrush CheckedHoverBrush = new ImmutableSolidColorBrush(Color.Parse("#FF7892"));
    private static readonly IBrush CheckedBackgroundBrush = new ImmutableSolidColorBrush(Color.Parse("#22FF5D7D"));
    private static readonly IBrush CheckedHoverBackgroundBrush = new ImmutableSolidColorBrush(Color.Parse("#32FF5D7D"));
    private static readonly IBrush TransparentBrush = new ImmutableSolidColorBrush(Colors.Transparent);

    private readonly HeartPath _outlinePath;
    private readonly HeartPath _filledPath;
    private bool _pointerOver;
    private bool _pressed;

    public FavoriteButton()
    {
        Width = 40;
        Height = 40;
        MinWidth = 36;
        MinHeight = 36;
        Padding = new Thickness(0);
        CornerRadius = new CornerRadius(12);
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;

        _outlinePath = new HeartPath
        {
            Data = Geometry.Parse(OutlineGeometryData),
            Fill = Brushes.Transparent,
            StrokeThickness = 1.8,
            Stretch = Stretch.Uniform,
            Width = 22,
            Height = 22,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        _filledPath = new HeartPath
        {
            Data = Geometry.Parse(FilledGeometryData),
            Stretch = Stretch.Uniform,
            Width = 22,
            Height = 22,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        Content = new Grid { Width = 22, Height = 22, Children = { _outlinePath, _filledPath } };
        UpdateVisualState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCheckedProperty || change.Property == IsEnabledProperty || change.Property == AutomationContextProperty)
            UpdateVisualState();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _pointerOver = true;
        UpdateVisualState();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerOver = false;
        _pressed = false;
        UpdateVisualState();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _pressed = true;
        UpdateVisualState();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _pressed = false;
        UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        if (_outlinePath is null || _filledPath is null) return;
        var favorite = IsChecked is true;
        var foreground = favorite
            ? (_pointerOver ? CheckedHoverBrush : CheckedBrush)
            : (_pointerOver ? UncheckedHoverBrush : UncheckedBrush);

        Foreground = foreground;
        Background = favorite
            ? (_pointerOver ? CheckedHoverBackgroundBrush : CheckedBackgroundBrush)
            : TransparentBrush;
        _outlinePath.IsVisible = !favorite;
        _outlinePath.Stroke = foreground;
        _filledPath.IsVisible = favorite;
        _filledPath.Fill = foreground;
        Opacity = !IsEnabled ? 0.58 : _pressed ? 0.78 : 1;

        var accessibleText = favorite ? RemoveTooltip : AddTooltip;
        ToolTip.SetTip(this, accessibleText);
        AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(AutomationContext) ? accessibleText : $"{accessibleText}，{AutomationContext}");
    }
}
