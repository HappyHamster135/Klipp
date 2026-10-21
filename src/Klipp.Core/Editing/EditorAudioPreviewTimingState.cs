namespace Klipp.Core.Editing;

/// <summary>Audio mix edits keep the decoder clock; only a changed source-to-timeline map needs a seek.</summary>
public sealed class EditorAudioPreviewTimingState
{
    private (double Start, double TrimStart, double Speed)? _mapping;

    public bool Update(double timelineStartSeconds, double trimStartSeconds, double playbackSpeed)
    {
        var mapping = (timelineStartSeconds, trimStartSeconds, playbackSpeed);
        var changed = _mapping != mapping;
        _mapping = mapping;
        return changed;
    }
}
