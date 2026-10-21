namespace Klipp.Core.Editing;

public static class EditorTimelineNavigationMath
{
    public static double AdjacentBoundary(IEnumerable<double> boundaries, double currentSeconds,
        double durationSeconds, double frameRate, bool next)
    {
        var epsilon = 0.5 / Math.Max(1, frameRate);
        var candidates = boundaries.Where(double.IsFinite)
            .Select(time => Math.Clamp(time, 0, Math.Max(0, durationSeconds)))
            .Append(0).Append(Math.Max(0, durationSeconds));
        return next
            ? candidates.Where(time => time > currentSeconds + epsilon)
                .DefaultIfEmpty(Math.Max(0, durationSeconds)).Min()
            : candidates.Where(time => time < currentSeconds - epsilon).DefaultIfEmpty(0).Max();
    }
}
