namespace Klipp.Desktop;

public sealed class EditorShortcutViewModel
{
    public EditorShortcutViewModel(string action, string label, string gestureDisplay)
    {
        Action = action;
        Label = label;
        GestureDisplay = gestureDisplay;
    }

    public string Action { get; set; }
    public string Label { get; set; }
    public string GestureDisplay { get; set; }
}
