namespace Klipp.Core.Editing;

public static class EditorTimelineInteractionMath
{
    public static int FindNearestBoundaryIndex(double pointerX, IReadOnlyList<double> boundaryXs,
        double railLeft, double railWidth, double hitRadius = 36)
    {
        if (!double.IsFinite(pointerX) || railWidth <= 0 || pointerX < railLeft || pointerX > railLeft + railWidth)
            return -1;
        var result = -1;
        var nearest = Math.Max(0, hitRadius);
        for (var index = 0; index < boundaryXs.Count; index++)
        {
            var boundary = boundaryXs[index];
            if (!double.IsFinite(boundary) || boundary < railLeft || boundary > railLeft + railWidth)
                continue;
            var distance = Math.Abs(pointerX - boundary);
            if (distance <= nearest && (result < 0 || distance < nearest))
            {
                nearest = distance;
                result = index;
            }
        }
        return result;
    }

    public static double AutoPanDeltaSeconds(double pointerX, double railLeft, double railWidth,
        double visibleDurationSeconds, double elapsedSeconds, double edgeZone = 40)
    {
        if (!double.IsFinite(pointerX) || !double.IsFinite(visibleDurationSeconds) ||
            !double.IsFinite(elapsedSeconds) || railWidth <= 0 || visibleDurationSeconds <= 0 || elapsedSeconds <= 0)
            return 0;
        var zone = Math.Min(Math.Max(1, edgeZone), railWidth / 3);
        var leftIntensity = Math.Clamp((railLeft + zone - pointerX) / zone, 0, 1);
        var rightIntensity = Math.Clamp((pointerX - (railLeft + railWidth - zone)) / zone, 0, 1);
        return (rightIntensity * rightIntensity - leftIntensity * leftIntensity) *
            visibleDurationSeconds * 0.6 * Math.Min(elapsedSeconds, 0.1);
    }

    public static double TrimEdgeHitWidth(double clipWidth, double desiredWidth = 16, double minimumBodyWidth = 18)
    {
        if (!double.IsFinite(clipWidth) || clipWidth <= 0)
            return 0;
        var bodyWidth = Math.Min(Math.Max(0, minimumBodyWidth), clipWidth / 2);
        return Math.Min(Math.Max(0, desiredWidth), (clipWidth - bodyWidth) / 2);
    }
}
