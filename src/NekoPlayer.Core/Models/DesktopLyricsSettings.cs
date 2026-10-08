namespace NekoPlayer.Core.Models;

/// <summary>Desktop subtitle preferences. Coordinates and screen work areas use physical pixels.</summary>
public sealed class DesktopLyricsSettings
{
    public bool IsEnabled { get; set; }
    public bool IsLocked { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public double Width { get; set; } = 720;
    public double Height { get; set; } = 144;
    public double FontSize { get; set; } = 28;
    public double Opacity { get; set; } = 1;
    public string? ScreenName { get; set; }
    public double ScreenScaling { get; set; } = 1;
    public int ScreenWorkAreaX { get; set; }
    public int ScreenWorkAreaY { get; set; }

    public DesktopLyricsSettings Copy() => (DesktopLyricsSettings)MemberwiseClone();
}
