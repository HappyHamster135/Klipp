namespace Klipp.Core.Editing;

public readonly record struct EditorWaveformPlacement(double Left, double Width);

/// <summary>Places a cached source interval without stretching it to the current clip or viewport.</summary>
public static class EditorWaveformMath
{
    public static bool TryPlace(double clipTimelineStart, double trimStart, double trimEnd,
        double playbackSpeed, double cachedStart, double cachedEnd, double viewportStart,
        double viewportDuration, double railWidth, out EditorWaveformPlacement placement)
    {
        placement = default;
        if (!double.IsFinite(clipTimelineStart) || !double.IsFinite(trimStart) || !double.IsFinite(trimEnd) ||
            !double.IsFinite(playbackSpeed) || !double.IsFinite(cachedStart) || !double.IsFinite(cachedEnd) ||
            !double.IsFinite(viewportStart) || !double.IsFinite(viewportDuration) || !double.IsFinite(railWidth) ||
            clipTimelineStart < 0 || trimStart < 0 || cachedStart < 0 || viewportStart < 0 ||
            playbackSpeed <= 0 || trimEnd <= trimStart || cachedEnd <= cachedStart ||
            viewportDuration <= 0 || railWidth <= 0)
            return false;

        var overlapStart = Math.Max(trimStart, cachedStart);
        var overlapEnd = Math.Min(trimEnd, cachedEnd);
        var visibleStart = Math.Max(clipTimelineStart, viewportStart);
        var visibleEnd = Math.Min(clipTimelineStart + (trimEnd - trimStart) / playbackSpeed,
            viewportStart + viewportDuration);
        if (overlapEnd <= overlapStart || visibleEnd <= visibleStart)
            return false;
        var waveformStart = clipTimelineStart + (overlapStart - trimStart) / playbackSpeed;
        var waveformEnd = clipTimelineStart + (overlapEnd - trimStart) / playbackSpeed;
        if (waveformEnd <= visibleStart || waveformStart >= visibleEnd)
            return false;

        var pixelsPerSecond = railWidth / viewportDuration;
        placement = new EditorWaveformPlacement(
            (clipTimelineStart + (cachedStart - trimStart) / playbackSpeed - visibleStart) * pixelsPerSecond,
            (cachedEnd - cachedStart) / playbackSpeed * pixelsPerSecond);
        return double.IsFinite(placement.Left) && double.IsFinite(placement.Width);
    }
}
