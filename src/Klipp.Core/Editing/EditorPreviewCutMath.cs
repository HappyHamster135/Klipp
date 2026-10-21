namespace Klipp.Core.Editing;

public readonly record struct EditorPreviewCutSegment(
    double TimelineStartSeconds,
    double TrimStartSeconds,
    double TrimEndSeconds,
    double PlaybackSpeed)
{
    public double TimelineEndSeconds => TimelineStartSeconds +
        Math.Max(0, TrimEndSeconds - TrimStartSeconds) / Math.Max(0.25, PlaybackSpeed);
}

public static class EditorPreviewCutMath
{
    // A split in contiguous footage does not change the decoder's source clock.
    // Seeking at that boundary flushes frames which are already ready to display.
    public static bool CanContinueSource(EditorPreviewCutSegment outgoing,
        EditorPreviewCutSegment incoming, bool sameSource) =>
        sameSource &&
        Math.Abs(outgoing.PlaybackSpeed - incoming.PlaybackSpeed) < 0.0001 &&
        Math.Abs(outgoing.TimelineEndSeconds - incoming.TimelineStartSeconds) <= 0.001 &&
        Math.Abs(outgoing.TrimEndSeconds - incoming.TrimStartSeconds) <= 0.001;

    public static bool CanUsePreparedFrame(double preparedSourceSeconds, double requestedSourceSeconds,
        double speed, double frameRate) =>
        Math.Abs(preparedSourceSeconds - requestedSourceSeconds) / Math.Max(0.25, speed) <=
        Math.Max(0.12, 2 / Math.Max(1, frameRate));
}
