namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    /// <summary>Pause hidden preview playback without unloading media or changing the edit.</summary>
    public void PausePlayback()
    {
        PausePreview();
        DismissTransitionAudition();
        PauseSourceViewer();
        UpdatePlaybackUi();
    }
}
