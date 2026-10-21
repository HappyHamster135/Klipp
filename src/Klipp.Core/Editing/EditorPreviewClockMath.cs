namespace Klipp.Core.Editing;

public readonly record struct EditorPreviewSourceTime(
    double SourceSeconds,
    double TimelineStartSeconds,
    double TrimStartSeconds,
    double PlaybackSpeed);

public static class EditorPreviewClockMath
{
    public static double Advance(double projectSeconds, double elapsedSeconds,
        EditorPreviewSourceTime? source = null, bool decoderBlocked = false)
    {
        if (decoderBlocked)
            return Math.Max(0, projectSeconds);
        if (source is { } video)
            return Math.Max(Math.Max(0, projectSeconds), Math.Max(0, video.TimelineStartSeconds) +
                Math.Max(0, video.SourceSeconds - video.TrimStartSeconds) /
                Math.Max(0.25, video.PlaybackSpeed));
        return Math.Max(0, projectSeconds) + Math.Max(0, elapsedSeconds);
    }

    public static double ContinueAfterClipEnd(double projectSeconds,
        double clipStartSeconds, double clipDurationSeconds) =>
        Math.Max(projectSeconds, Math.Max(0, clipStartSeconds) + Math.Max(0, clipDurationSeconds));
}
