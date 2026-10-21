namespace Klipp.Core.Editing;

/// <summary>Source/time mapping shared by video and independently edited audio.</summary>
public static class EditorClipEditMath
{
    public static double MoveTimelineStart(double originalTimelineStartSeconds,
        double pointerStartSeconds, double pointerSeconds, double frameRate) =>
        EditorTimelineMath.SnapToFrame(Math.Max(0,
            originalTimelineStartSeconds + pointerSeconds - pointerStartSeconds), frameRate);

    public static double EarliestTrimStart(double timelineStartSeconds, double sourceTrimStartSeconds,
        double playbackSpeed) => Math.Max(0, timelineStartSeconds - sourceTrimStartSeconds / playbackSpeed);

    public static bool TrySplitSource(double playheadSeconds, double timelineStartSeconds,
        double trimStartSeconds, double trimEndSeconds, double playbackSpeed,
        out double sourceSeconds, double minimumSourceDuration = 0.1)
    {
        sourceSeconds = 0;
        if (!double.IsFinite(playheadSeconds) || !double.IsFinite(timelineStartSeconds) ||
            !double.IsFinite(trimStartSeconds) || !double.IsFinite(trimEndSeconds) ||
            !double.IsFinite(playbackSpeed) || playbackSpeed <= 0)
            return false;

        sourceSeconds = trimStartSeconds + (playheadSeconds - timelineStartSeconds) * playbackSpeed;
        return sourceSeconds >= trimStartSeconds + minimumSourceDuration - 0.000001 &&
               sourceSeconds <= trimEndSeconds - minimumSourceDuration + 0.000001;
    }

    public static bool Contains(double playheadSeconds, double timelineStartSeconds,
        double trimStartSeconds, double trimEndSeconds, double playbackSpeed) =>
        double.IsFinite(playbackSpeed) && playbackSpeed > 0 &&
        playheadSeconds >= timelineStartSeconds &&
        playheadSeconds < timelineStartSeconds + (trimEndSeconds - trimStartSeconds) / playbackSpeed;
}
