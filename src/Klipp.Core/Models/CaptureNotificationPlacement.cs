namespace Klipp.Core.Models;

public readonly record struct CaptureNotificationBounds(int Left, int Top, int Width, int Height);

public static class CaptureNotificationPlacement
{
    /// <summary>Places the card near the left edge, slightly below the display's top corner.</summary>
    public static CaptureNotificationBounds UpperLeftEdge(
        int left, int top, int width, int height, double scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        scale = Math.Clamp(scale, .5, 8);
        var safeInset = Math.Max(8, (int)Math.Round(16 * scale));
        var horizontalInset = Math.Min(safeInset, (width - 1) / 2);
        var verticalInset = Math.Min(safeInset, (height - 1) / 2);
        var cardWidth = Math.Min((int)Math.Round(384 * scale), width - 2 * horizontalInset);
        var cardHeight = Math.Min((int)Math.Round(120 * scale), height - 2 * verticalInset);
        var topInset = Math.Clamp((int)Math.Round(56 * scale), verticalInset,
            height - verticalInset - cardHeight);
        return new CaptureNotificationBounds(
            left + horizontalInset, top + topInset, cardWidth, cardHeight);
    }
}
