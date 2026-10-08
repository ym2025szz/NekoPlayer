using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using NekoPlayer.App.Services;

namespace NekoPlayer.App.Controls;

/// <summary>A bounded virtual lyric list. Its own scroll viewer never moves the surrounding page.</summary>
public partial class LyricsViewport : UserControl
{
    public static readonly StyledProperty<LyricsPresentationService?> PresentationProperty =
        AvaloniaProperty.Register<LyricsViewport, LyricsPresentationService?>(nameof(Presentation));
    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<LyricsViewport, bool>(nameof(ReduceMotion));

    private readonly LyricsViewportSpacer _leadingSpace = new();
    private readonly LyricsViewportSpacer _trailingSpace = new();
    private readonly DispatcherTimer _resumeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _animationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private LyricsPresentationService? _subscribed;
    private ScrollViewer? _scrollViewer;
    private ScrollContentPresenter? _scrollContent;
    private bool _attached;
    private bool _wasVisible;
    private bool _manualBrowse;
    private bool _pointerBrowsing;
    private bool _followQueued;
    private bool _queuedImmediate;
    private int _followVersion;
    private int _animationVersion;
    private long _animationStart;
    private double _animationFrom;
    private double _animationTo;
    private int _animationIndex;

    public LyricsViewport()
    {
        InitializeComponent();
        _resumeTimer.Tick += OnResumeTimer;
        _animationTimer.Tick += OnAnimationTick;
        LyricsList.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel, true);
        LyricsList.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, true);
        LyricsList.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, true);
        LyricsList.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, true);
        LyricsList.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Bubble, true);
        LyricsList.AddHandler(InputElement.DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, true);
        UpdatePanels();
    }

    public LyricsPresentationService? Presentation
    {
        get => GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }
    public bool ReduceMotion
    {
        get => GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }
    public event EventHandler<LyricsJumpRequestedEventArgs>? JumpRequested;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PresentationProperty && LayoutRoot is not null) SubscribePresentation();
        else if (change.Property == DataContextProperty && Presentation is null && DataContext is LyricsPresentationService service)
            Presentation = service;
        else if (change.Property == ReduceMotionProperty && ReduceMotion) QueueFollow(true);
        else if (change.Property == BoundsProperty && LyricsList is not null &&
                 change.GetOldValue<Rect>().Size != change.GetNewValue<Rect>().Size)
        {
            UpdatePadding();
            QueueFollow(true);
        }
        else if (change.Property == IsVisibleProperty && IsVisible) QueueFollow(true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _wasVisible = IsEffectivelyVisible;
        LayoutUpdated += OnLayoutUpdated;
        SubscribePresentation();
        QueueFollow(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        LayoutUpdated -= OnLayoutUpdated;
        UnsubscribePresentation();
        _resumeTimer.Stop();
        CancelAnimation();
        _manualBrowse = false;
        _pointerBrowsing = false;
        _scrollViewer = null;
        _scrollContent = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribePresentation()
    {
        UnsubscribePresentation();
        LayoutRoot.DataContext = Presentation;
        if (_attached && Presentation is { } service)
        {
            _subscribed = service;
            service.PropertyChanged += OnPresentationPropertyChanged;
            service.CurrentChanged += OnCurrentChanged;
        }
        RebuildItems();
        UpdatePanels();
        QueueFollow(true);
    }

    private void UnsubscribePresentation()
    {
        if (_subscribed is not { } service) return;
        service.PropertyChanged -= OnPresentationPropertyChanged;
        service.CurrentChanged -= OnCurrentChanged;
        _subscribed = null;
    }

    private void OnPresentationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsPresentationService.Rows))
        {
            RebuildItems();
            QueueFollow(true);
        }
        if (e.PropertyName is nameof(LyricsPresentationService.HasLyrics) or nameof(LyricsPresentationService.Status))
            UpdatePanels();
        if (e.PropertyName == nameof(LyricsPresentationService.CanSeek)) UpdateSeekAffordance();
    }

    private void OnCurrentChanged(object? sender, LyricsCurrentChangedEventArgs e)
    {
        if (e.Immediate)
        {
            _manualBrowse = false;
            _resumeTimer.Stop();
        }
        QueueFollow(e.Immediate);
    }

    private void RebuildItems()
    {
        CancelAnimation();
        var rows = Presentation?.Rows;
        if (rows is null || rows.Count == 0) { LyricsList.ItemsSource = Array.Empty<object>(); return; }
        var items = new object[rows.Count + 2];
        items[0] = _leadingSpace;
        for (var index = 0; index < rows.Count; index++) items[index + 1] = rows[index];
        items[^1] = _trailingSpace;
        LyricsList.ItemsSource = items;
        UpdatePadding();
    }

    private void UpdatePanels()
    {
        var hasLyrics = Presentation?.HasLyrics == true;
        LyricsList.IsVisible = hasLyrics;
        StatePanel.IsVisible = !hasLyrics;
        RetainedErrorPanel.IsVisible = hasLyrics && Presentation?.IsError == true;
        UpdateSeekAffordance();
    }

    private void UpdateSeekAffordance()
    {
        var canSeek = Presentation?.CanSeek == true;
        ToolTip.SetTip(LyricsList, canSeek ? "双击一句歌词跳转播放位置" : "当前音源暂不支持跳转");
        AutomationProperties.SetName(LyricsList, canSeek ? "歌曲歌词，双击一句跳转播放位置" : "歌曲歌词");
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var visible = IsEffectivelyVisible;
        if (visible && !_wasVisible) QueueFollow(true);
        _wasVisible = visible;
    }

    private void UpdatePadding()
    {
        // Real spacer items are part of the virtualized extent, so either end can be centered.
        var half = Math.Max(0, (_scrollViewer?.Viewport.Height ?? Bounds.Height) / 2);
        _leadingSpace.Height = half;
        _trailingSpace.Height = half;
    }

    private void QueueFollow(bool immediate)
    {
        if (!_attached || !IsEffectivelyVisible || (_manualBrowse && !immediate)) return;
        _queuedImmediate |= immediate || ReduceMotion;
        CancelAnimation();
        if (_followQueued) return;
        _followQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _followQueued = false;
            var jump = _queuedImmediate;
            _queuedImmediate = false;
            if (!_attached || !IsEffectivelyVisible || (_manualBrowse && !jump)) return;
            _scrollViewer ??= LyricsList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            _scrollContent ??= _scrollViewer?.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
            UpdatePadding();
            CenterCurrent(jump, ++_followVersion, 0);
        }, DispatcherPriority.Loaded);
    }

    private void CenterCurrent(bool immediate, int version, int attempt)
    {
        if (version != _followVersion || !_attached || _scrollViewer is null || Presentation is not { HasLyrics: true } service) return;
        var index = Math.Max(0, service.CurrentIndex) + 1;
        if (LyricsList.ContainerFromIndex(index) is not { } container)
        {
            if (attempt >= 2) return;
            LyricsList.ScrollIntoView(index);
            Dispatcher.UIThread.Post(() => CenterCurrent(immediate, version, attempt + 1), DispatcherPriority.Loaded);
            return;
        }
        var target = GetCenterOffset(container);
        if (target is null) return;
        if (immediate || ReduceMotion || Math.Abs(target.Value - _scrollViewer.Offset.Y) < 1)
        {
            _scrollViewer.Offset = new(_scrollViewer.Offset.X, target.Value);
            return;
        }
        _animationFrom = _scrollViewer.Offset.Y;
        _animationTo = target.Value;
        _animationIndex = index;
        _animationVersion = version;
        _animationStart = Stopwatch.GetTimestamp();
        _animationTimer.Start();
    }

    private double? GetCenterOffset(Control container)
    {
        if (_scrollViewer is null) return null;
        Visual viewport = _scrollContent is { } content ? content : _scrollViewer;
        var transform = container.TransformToVisual(viewport);
        if (transform is null) return null;
        var center = transform.Value.Transform(new Point(0, container.Bounds.Height / 2)).Y;
        var viewportHeight = _scrollViewer.Viewport.Height;
        var offset = _scrollViewer.Offset.Y + center - viewportHeight / 2;
        return Math.Clamp(offset, 0, Math.Max(0, _scrollViewer.Extent.Height - viewportHeight));
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (!_attached || _scrollViewer is null || _animationVersion != _followVersion || _manualBrowse)
        { _animationTimer.Stop(); return; }
        var progress = Math.Clamp(Stopwatch.GetElapsedTime(_animationStart).TotalMilliseconds / 200, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        _scrollViewer.Offset = new(_scrollViewer.Offset.X, _animationFrom + (_animationTo - _animationFrom) * eased);
        if (progress < 1) return;
        _animationTimer.Stop();
        // Virtualized wrapped rows can refine the extent while they are realized.
        var version = _animationVersion;
        var index = _animationIndex;
        Dispatcher.UIThread.Post(() =>
        {
            if (version == _followVersion && !_manualBrowse && LyricsList.ContainerFromIndex(index) is { } row &&
                GetCenterOffset(row) is { } target && _scrollViewer is not null)
                _scrollViewer.Offset = new(_scrollViewer.Offset.X, target);
        }, DispatcherPriority.Loaded);
    }

    private void CancelAnimation()
    {
        _animationTimer.Stop();
        _followVersion++;
    }

    private void BrowseManually()
    {
        _manualBrowse = true;
        _queuedImmediate = false;
        CancelAnimation();
        _resumeTimer.Stop();
        _resumeTimer.Start();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e) => BrowseManually();
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch || IsScrollBarSource(e.Source))
        {
            _pointerBrowsing = true;
            BrowseManually();
        }
    }
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pointerBrowsing) BrowseManually();
    }
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pointerBrowsing) return;
        _pointerBrowsing = false;
        BrowseManually();
    }
    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_pointerBrowsing) return;
        _pointerBrowsing = false;
        BrowseManually();
    }
    private static bool IsScrollBarSource(object? source) => source is Visual visual &&
        (visual is ScrollBar || visual.GetVisualAncestors().Any(parent => parent is ScrollBar));

    private void OnResumeTimer(object? sender, EventArgs e)
    {
        if (_pointerBrowsing) return;
        _resumeTimer.Stop();
        _manualBrowse = false;
        QueueFollow(false);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Presentation is not { CanSeek: true } service || e.Source is not Visual source) return;
        var container = source is ListBoxItem direct ? direct : source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (container?.DataContext is not LyricsPresentationRow row) return;
        _manualBrowse = false;
        _resumeTimer.Stop();
        service.RequestImmediateFollow();
        JumpRequested?.Invoke(this, new(row.Timestamp));
        e.Handled = true;
    }
}

public sealed class LyricsJumpRequestedEventArgs(TimeSpan timestamp) : EventArgs
{
    public TimeSpan Timestamp { get; } = timestamp;
}

public sealed class LyricsViewportSpacer : ObservableObject
{
    private double _height;
    public double Height { get => _height; internal set => SetProperty(ref _height, value); }
}
