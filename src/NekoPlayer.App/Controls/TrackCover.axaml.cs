using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NekoPlayer.App.Controls;

public partial class TrackCover : UserControl
{
    public static readonly StyledProperty<string?> CoverPathProperty =
        AvaloniaProperty.Register<TrackCover, string?>(nameof(CoverPath));

    public static readonly StyledProperty<string?> CoverUrlProperty =
        AvaloniaProperty.Register<TrackCover, string?>(nameof(CoverUrl));

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<TrackCover, int>(nameof(DecodeWidth), 160);

    public static readonly StyledProperty<CornerRadius> CoverCornerRadiusProperty =
        AvaloniaProperty.Register<TrackCover, CornerRadius>(nameof(CoverCornerRadius), new CornerRadius(12));

    public static readonly StyledProperty<Stretch> CoverStretchProperty =
        AvaloniaProperty.Register<TrackCover, Stretch>(nameof(CoverStretch), Stretch.UniformToFill);

    private int _loadGeneration;
    private Bitmap? _ownedBitmap;
    private CancellationTokenSource? _loadCancellation;
    private bool _isAttached;
    private bool _refreshQueued;
    private (string? Path, string? Url, int Width)? _requestedCover;

    public TrackCover()
    {
        InitializeComponent();
        CoverBorder.CornerRadius = CoverCornerRadius;
        ShowDefaultCover();
    }

    public string? CoverPath
    {
        get => GetValue(CoverPathProperty);
        set => SetValue(CoverPathProperty, value);
    }

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public string? CoverUrl
    {
        get => GetValue(CoverUrlProperty);
        set => SetValue(CoverUrlProperty, value);
    }

    public CornerRadius CoverCornerRadius
    {
        get => GetValue(CoverCornerRadiusProperty);
        set => SetValue(CoverCornerRadiusProperty, value);
    }

    public Stretch CoverStretch
    {
        get => GetValue(CoverStretchProperty);
        set => SetValue(CoverStretchProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CoverCornerRadiusProperty && CoverBorder is not null)
            CoverBorder.CornerRadius = CoverCornerRadius;
        else if (change.Property == CoverPathProperty || change.Property == CoverUrlProperty || change.Property == DecodeWidthProperty)
            QueueRefresh();
        else if (change.Property == CoverStretchProperty && _ownedBitmap is not null)
            CoverImage.Stretch = CoverStretch;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        QueueRefresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        CancelLoad();
        _requestedCover = null;
        DisposeOwnedBitmap();
        base.OnDetachedFromVisualTree(e);
    }

    private void QueueRefresh()
    {
        if (!_isAttached || CoverImage is null || _refreshQueued) return;
        _refreshQueued = true;
        // Bindings may set path, URL and width together; only decode their final values once.
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            var request = (CoverPath, CoverUrl, DecodeWidth);
            if (!_isAttached || request == _requestedCover) return;
            var sameSource = _requestedCover is { } previous && previous.Path == request.CoverPath && previous.Url == request.CoverUrl;
            CancelLoad();
            _requestedCover = request;
            _ = RefreshCoverAsync(sameSource);
        });
    }

    private void CancelLoad()
    {
        Interlocked.Increment(ref _loadGeneration);
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task RefreshCoverAsync(bool sameSource)
    {
        if (!_isAttached || CoverImage is null) return;
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;
        var generation = Interlocked.Increment(ref _loadGeneration);
        var path = CoverPath;
        var url = CoverUrl;
        var decodeWidth = DecodeWidth;
        // A size update may refine the same image without displaying a placeholder first.
        if (!sameSource) ShowDefaultCover();

        TrackCoverResult? result = null;
        var ownershipTransferred = false;
        try
        {
            result = await TrackCoverProvider.Shared.GetCoverAsync(
                string.IsNullOrWhiteSpace(path) ? url : path, decodeWidth, token).ConfigureAwait(false);
            if (result.IsDefault && !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(url) &&
                !string.Equals(path, url, StringComparison.Ordinal))
                result = await TrackCoverProvider.Shared.GetCoverAsync(url, decodeWidth, token).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_isAttached || token.IsCancellationRequested || generation != Volatile.Read(ref _loadGeneration)) return;
                DisposeOwnedBitmap();
                CoverImage.Source = result.Image;
                CoverImage.Stretch = result.IsDefault ? Stretch.Uniform : CoverStretch;
                AutomationProperties.SetName(this, result.IsDefault ? "猫娘播放器默认歌曲封面" : "歌曲封面");
                if (!result.IsDefault) _ownedBitmap = result.Image;
                ownershipTransferred = true;
            });
        }
        catch (OperationCanceledException)
        {
            // A newer cover, or a detached item, owns the display now.
        }
        finally
        {
            if (!ownershipTransferred && result is { IsDefault: false }) result.Image?.Dispose();
            Interlocked.CompareExchange(ref _loadCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void ShowDefaultCover()
    {
        if (CoverImage is null) return;
        DisposeOwnedBitmap();
        CoverImage.Source = TrackCoverProvider.Shared.DefaultCover;
        CoverImage.Stretch = Stretch.Uniform;
        AutomationProperties.SetName(this, "猫娘播放器默认歌曲封面");
    }

    private void DisposeOwnedBitmap()
    {
        if (_ownedBitmap is null) return;
        if (ReferenceEquals(CoverImage.Source, _ownedBitmap)) CoverImage.Source = null;
        _ownedBitmap.Dispose();
        _ownedBitmap = null;
    }
}
