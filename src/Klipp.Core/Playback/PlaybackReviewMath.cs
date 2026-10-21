namespace Klipp.Core.Playback;

public static class PlaybackReviewMath
{
    public static double NextRate(double currentRate, int direction)
    {
        if (direction == 0) return 0;
        var sign = Math.Sign(direction);
        return double.IsFinite(currentRate) && Math.Sign(currentRate) == sign
            ? sign * Math.Min(8, Math.Max(1, Math.Abs(currentRate) * 2)) : sign;
    }

    public static double Advance(double originSeconds, double elapsedSeconds, double rate, double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || end < start) return 0;
        if (!double.IsFinite(originSeconds) || !double.IsFinite(elapsedSeconds) || !double.IsFinite(rate)) return start;
        return Math.Clamp(originSeconds + Math.Max(0, elapsedSeconds) * rate, start, end);
    }

    public static string FormatRate(double rate) => rate < 0 ? $"Reverse {Math.Abs(rate):0}×" : $"{rate:0}×";
}
