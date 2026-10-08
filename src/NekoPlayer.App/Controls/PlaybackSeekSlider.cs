using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using NekoPlayer.Core.Services;

namespace NekoPlayer.App.Controls;

public sealed class PlaybackSeekSlider : TemplatedControl
{
    private const double TrackHeight = 4;
    private const double ThumbDiameter = 12;
    private IPointer? _activePointer;
    private PlaybackSeekSliderAutomationPeer? _automationPeer;

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<PlaybackSeekSlider, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<PlaybackSeekSlider, double>(nameof(Maximum), 100d);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<PlaybackSeekSlider, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> ProgressBrushProperty =
        AvaloniaProperty.Register<PlaybackSeekSlider, IBrush?>(
            nameof(ProgressBrush),
            new SolidColorBrush(Color.FromRgb(255, 127, 153)));

    public static readonly StyledProperty<IBrush?> ThumbBrushProperty =
        AvaloniaProperty.Register<PlaybackSeekSlider, IBrush?>(nameof(ThumbBrush), Brushes.White);

    static PlaybackSeekSlider()
    {
        FocusableProperty.OverrideDefaultValue<PlaybackSeekSlider>(true);
        AffectsRender<PlaybackSeekSlider>(
            MinimumProperty,
            MaximumProperty,
            ValueProperty,
            BackgroundProperty,
            ProgressBrushProperty,
            ThumbBrushProperty);
    }

    public PlaybackSeekSlider()
    {
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? ProgressBrush
    {
        get => GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public IBrush? ThumbBrush
    {
        get => GetValue(ThumbBrushProperty);
        set => SetValue(ThumbBrushProperty, value);
    }

    public bool IsSeeking => _activePointer is not null;

    public event EventHandler<SeekValueEventArgs>? SeekStarted;
    public event EventHandler<SeekValueEventArgs>? SeekPreviewChanged;
    public event EventHandler<SeekValueEventArgs>? SeekCompleted;
    public event EventHandler<SeekValueEventArgs>? SeekCancelled;

    private bool CanInteract => IsEnabled && double.IsFinite(Maximum) && double.IsFinite(Minimum) && Maximum > Minimum;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        _automationPeer = new PlaybackSeekSliderAutomationPeer(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var property = change.Property == ValueProperty ? RangeValuePatternIdentifiers.ValueProperty
            : change.Property == MinimumProperty ? RangeValuePatternIdentifiers.MinimumProperty
            : change.Property == MaximumProperty ? RangeValuePatternIdentifiers.MaximumProperty : null;
        if (property is not null)
            _automationPeer?.RaisePropertyChangedEvent(property, change.OldValue, change.NewValue);
        if (change.Property == IsEnabledProperty || change.Property == MinimumProperty || change.Property == MaximumProperty)
            _automationPeer?.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.IsReadOnlyProperty, null, !CanInteract);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        // A full-bounds draw operation makes the complete 22 px control surface participate
        // in hit testing instead of relying on the narrow default Slider template track.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var padding = Math.Clamp(SeekPointerMath.DefaultEdgePadding, 0, width / 2);
        var usableWidth = Math.Max(1, width - padding * 2);
        var trackTop = Math.Max(0, (height - TrackHeight) / 2);
        var trackRect = new Rect(padding, trackTop, usableWidth, Math.Min(TrackHeight, height));
        var ratio = GetValueRatio();
        var progressWidth = usableWidth * ratio;
        var trackBrush = Background ?? new SolidColorBrush(Color.FromRgb(42, 65, 70));
        var progressBrush = ProgressBrush ?? new SolidColorBrush(Color.FromRgb(255, 127, 153));
        var thumbBrush = ThumbBrush ?? Brushes.White;

        context.DrawRectangle(trackBrush, null, trackRect, TrackHeight / 2, TrackHeight / 2);
        if (progressWidth > 0)
        {
            context.DrawRectangle(
                progressBrush,
                null,
                new Rect(padding, trackTop, progressWidth, trackRect.Height),
                TrackHeight / 2,
                TrackHeight / 2);
        }

        var thumbCenterX = padding + progressWidth;
        var thumbRect = new Rect(
            thumbCenterX - ThumbDiameter / 2,
            (height - ThumbDiameter) / 2,
            ThumbDiameter,
            ThumbDiameter);
        context.DrawEllipse(thumbBrush, null, thumbRect);

        if (IsKeyboardFocusWithin)
        {
            context.DrawRectangle(
                null,
                new Pen(progressBrush, 1),
                new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)),
                4,
                4);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanInteract || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (_activePointer is not null && !ReferenceEquals(_activePointer, e.Pointer)) return;

        if (_activePointer is null)
        {
            _activePointer = e.Pointer;
            Focus();
            SeekStarted?.Invoke(this, new SeekValueEventArgs(Value));
            e.Pointer.Capture(this);
        }

        UpdatePreview(e.GetPosition(this).X);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_activePointer, e.Pointer)) return;
        UpdatePreview(e.GetPosition(this).X);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_activePointer, e.Pointer)) return;
        UpdatePreview(e.GetPosition(this).X);
        _activePointer = null;
        e.Pointer.Capture(null);
        SeekCompleted?.Invoke(this, new SeekValueEventArgs(Value));
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_activePointer is null) return;
        var previous = Value;
        _activePointer = null;
        SeekCancelled?.Invoke(this, new SeekValueEventArgs(previous));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!CanInteract || !IsKeyboardFocusWithin) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 15d : 5d;
        var target = e.Key switch
        {
            Key.Left => Value - step,
            Key.Right => Value + step,
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => double.NaN
        };
        if (!double.IsFinite(target)) return;

        SeekStarted?.Invoke(this, new SeekValueEventArgs(Value));
        SetCurrentValue(ValueProperty, Math.Clamp(target, Minimum, Maximum));
        SeekPreviewChanged?.Invoke(this, new SeekValueEventArgs(Value));
        SeekCompleted?.Invoke(this, new SeekValueEventArgs(Value));
        e.Handled = true;
    }

    private double GetValueRatio()
    {
        if (!double.IsFinite(Minimum) || !double.IsFinite(Maximum) || Maximum <= Minimum) return 0;
        return Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1);
    }

    private void UpdatePreview(double x)
    {
        SetCurrentValue(ValueProperty, SeekPointerMath.ValueFromX(x, Bounds.Width, Minimum, Maximum));
        SeekPreviewChanged?.Invoke(this, new SeekValueEventArgs(Value));
    }

    private sealed class PlaybackSeekSliderAutomationPeer(PlaybackSeekSlider owner)
        : ControlAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(PlaybackSeekSlider);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetLocalizedControlTypeCore() => "播放进度";
        protected override string? GetHelpTextCore() => "以秒为单位；方向键移动 5 秒，Shift 加方向键移动 15 秒。";
        public bool IsReadOnly => !owner.CanInteract;
        public double Minimum => double.IsFinite(owner.Minimum) ? owner.Minimum : 0;
        public double Maximum => double.IsFinite(owner.Maximum) ? Math.Max(Minimum, owner.Maximum) : Minimum;
        public double Value => double.IsFinite(owner.Value) ? Math.Clamp(owner.Value, Minimum, Maximum) : Minimum;
        public double SmallChange => 5;
        public double LargeChange => 15;

        public void SetValue(double value)
        {
            EnsureEnabled();
            if (IsReadOnly) throw new InvalidOperationException("当前播放进度不可调整。");
            if (!double.IsFinite(value) || value < Minimum || value > Maximum)
                throw new ArgumentOutOfRangeException(nameof(value));
            owner.SeekStarted?.Invoke(owner, new SeekValueEventArgs(owner.Value));
            owner.SetCurrentValue(ValueProperty, value);
            owner.SeekPreviewChanged?.Invoke(owner, new SeekValueEventArgs(value));
            owner.SeekCompleted?.Invoke(owner, new SeekValueEventArgs(value));
        }
    }
}

public sealed class SeekValueEventArgs(double value) : EventArgs
{
    public double Value { get; } = value;
}
