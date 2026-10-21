namespace Klipp.Core.Editing;

/// <summary>Viewport calculations shared by timeline navigation and its ruler.</summary>
public static class EditorTimelineMath
{
    public static double SnapToFrame(double seconds, double frameRate) =>
        Math.Round(seconds * frameRate, MidpointRounding.AwayFromZero) / frameRate;

    public static double PointerTime(double pointerX, double railLeft, double railWidth,
        double visibleStartSeconds, double visibleDurationSeconds)
    {
        var ratio = Math.Clamp((pointerX - railLeft) / Math.Max(1, railWidth), 0, 1);
        return visibleStartSeconds + ratio * Math.Max(0, visibleDurationSeconds);
    }

    public static double ZoomViewportStart(double anchorSeconds, double anchorRatio,
        double visibleDurationSeconds, double durationSeconds) =>
        Math.Clamp(anchorSeconds - Math.Clamp(anchorRatio, 0, 1) * visibleDurationSeconds,
            0, Math.Max(0, durationSeconds - visibleDurationSeconds));

    public static double MajorTickSeconds(double visibleDurationSeconds, double railWidth)
    {
        // Keep labels readable, and keep the number of ruler elements proportional to
        // the viewport width even when editing recordings that are several hours long.
        var target = Math.Max(0.1, visibleDurationSeconds * 76 / Math.Max(1, railWidth));
        foreach (var interval in new[] { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120,
                     300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400 })
        {
            if (interval >= target)
                return interval;
        }
        return Math.Ceiling(target / 86400) * 86400;
    }

    public static double EdgeHitWidth(double clipWidth, double maximumWidth = 12) =>
        Math.Min(Math.Max(0, maximumWidth), Math.Max(0, clipWidth) / 3);
}
