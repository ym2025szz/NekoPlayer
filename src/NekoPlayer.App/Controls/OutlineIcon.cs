using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NekoPlayer.App.Controls;

/// <summary>A consistent 24-unit outline grid, including the empty margins around each glyph.</summary>
public sealed class OutlineIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<OutlineIcon, string>(nameof(Kind), "Home");
    public static readonly StyledProperty<IBrush?> ForegroundProperty = Avalonia.Controls.Documents.TextElement.ForegroundProperty.AddOwner<OutlineIcon>();
    private static readonly Dictionary<string, Geometry> Geometries = new()
    {
        ["Home"] = Geometry.Parse("M3,10 L12,3 L21,10 M5,9 V21 H10 V14 H14 V21 H19 V9"),
        ["Library"] = Geometry.Parse("M9,18 V5 L20,3 V16 M9,5 V9 L20,7 M9,18 A3,3 0 1 1 6,15 A3,3 0 0 1 9,18 M20,16 A3,3 0 1 1 17,13 A3,3 0 0 1 20,16"),
        ["NowPlaying"] = Geometry.Parse("M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M10,8 L16,12 L10,16 Z"),
        ["Search"] = Geometry.Parse("M18,10 A8,8 0 1 1 2,10 A8,8 0 1 1 18,10 M16,16 L22,22"),
        ["History"] = Geometry.Parse("M3,11 A9,9 0 1 1 5,18 M3,4 V11 H10 M12,7 V12 L16,14"),
        ["Favorite"] = Geometry.Parse("M12,21 L4,13 C-2,7 5,0 12,7 C19,0 26,7 20,13 Z"),
        ["Playlist"] = Geometry.Parse("M5,3 H19 V21 H5 Z M8,7 H16 M8,12 H16 M8,17 H14"),
        ["Settings"] = Geometry.Parse("M9,3 H15 L16,6 L19,7 L21,10 L19,12 L21,15 L19,18 L16,18 L15,21 H9 L8,18 L5,18 L3,15 L5,12 L3,10 L5,7 L8,6 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12"),
        ["Account"] = Geometry.Parse("M16,7 A4,4 0 1 1 8,7 A4,4 0 1 1 16,7 M4,21 V18 C4,11 20,11 20,18 V21"),
    };
    static OutlineIcon() => AffectsRender<OutlineIcon>(KindProperty, ForegroundProperty);
    public OutlineIcon() { Width = 20; Height = 20; HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center; IsHitTestVisible = false; }
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public override void Render(DrawingContext context)
    {
        if (!Geometries.TryGetValue(Kind, out var geometry)) return;
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24)))
            context.DrawGeometry(null, new Pen(Foreground, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }
}
