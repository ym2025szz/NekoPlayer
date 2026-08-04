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

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<TrackCover, int>(nameof(DecodeWidth), 160);

    public static readonly StyledProperty<CornerRadius> CoverCornerRadiusProperty =
        AvaloniaProperty.Register<TrackCover, CornerRadius>(nameof(CoverCornerRadius), new CornerRadius(12));

    public static readonly StyledProperty<Stretch> CoverStretchProperty =
        AvaloniaProperty.Register<TrackCover, Stretch>(nameof(CoverStretch), Stretch.UniformToFill);

    private int _loadGeneration;
    private Bitmap? _ownedBitmap;

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
        else if (change.Property == CoverPathProperty || change.Property == DecodeWidthProperty)
            _ = RefreshCoverAsync();
        else if (change.Property == CoverStretchProperty && _ownedBitmap is not null)
            CoverImage.Stretch = CoverStretch;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = RefreshCoverAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Interlocked.Increment(ref _loadGeneration);
        DisposeOwnedBitmap();
        base.OnDetachedFromVisualTree(e);
    }

    private async Task RefreshCoverAsync()
    {
        if (CoverImage is null) return;
        var generation = Interlocked.Increment(ref _loadGeneration);
        ShowDefaultCover();

        TrackCoverResult result;
        try
        {
            result = await TrackCoverProvider.Shared.GetCoverAsync(CoverPath, DecodeWidth).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _loadGeneration)
            {
                if (!result.IsDefault) result.Image?.Dispose();
                return;
            }

            DisposeOwnedBitmap();
            CoverImage.Source = result.Image;
            CoverImage.Stretch = result.IsDefault ? Stretch.Uniform : CoverStretch;
            AutomationProperties.SetName(this, result.IsDefault ? "猫娘播放器默认歌曲封面" : "歌曲封面");
            if (!result.IsDefault) _ownedBitmap = result.Image;
        });
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
