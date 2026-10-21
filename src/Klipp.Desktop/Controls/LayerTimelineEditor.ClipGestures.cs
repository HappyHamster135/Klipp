using System;
using System.Diagnostics;
using Klipp.Core.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private readonly EditorClipDoubleClickGate _clipDoubleClick = new();
    private bool _dragEditStartingRaised;
    public bool LastDragWasSelectionOnly { get; private set; }

    private static bool DefersEditUntilDrag(DragMode mode) => mode is
        DragMode.MediaMove or DragMode.MediaSlip or DragMode.MediaTrimStart or DragMode.MediaTrimEnd or
        DragMode.AudioMove or DragMode.AudioSlip or DragMode.AudioTrimStart or DragMode.AudioTrimEnd or
        DragMode.AudioAutomation or DragMode.TransitionMove or DragMode.TransitionDuration or
        DragMode.EffectMove or DragMode.EffectStart or DragMode.EffectEnd;

    private bool TryHandleClipDoublePress(string id, bool audio, DragMode mode, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TimelineSurface).Position;
        var body = mode is DragMode.MediaMove or DragMode.MediaSlip or DragMode.AudioMove or DragMode.AudioSlip;
        var alt = IsKeyDown(VirtualKey.Menu);
        if (!_clipDoubleClick.Press($"{(audio ? "audio" : "video")}:{id}", Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
                point.X, point.Y, alt ? 1 : 0, body))
            return false;
        Focus(FocusState.Pointer);
        _selectedMediaClipIds.Clear();
        _selectedAudioTrackIds.Clear();
        SelectForPointer(id, audio, toggle: false);
        if (audio)
            _selectedAudioTrackId = id;
        if (audio && alt)
            AddAudioVolumePoint(id, point);
        else
        {
            var position = SnapTime(TimeFromX(point.X));
            if (!SplitAtPosition(position))
                SetEditStatus(string.IsNullOrWhiteSpace(LastEditStatus)
                    ? "Double-click inside the clip, away from its edges, to split it." : LastEditStatus);
            else
                SetEditStatus($"Split at {position.TotalSeconds:0.###}s.");
        }
        e.Handled = true;
        return true;
    }

    private void CompleteClipClick(DragMode mode) => _clipDoubleClick.Release(
        mode is DragMode.MediaMove or DragMode.MediaSlip or DragMode.AudioMove or DragMode.AudioSlip or DragMode.EffectMove && !_pointerDragActivated);
}
