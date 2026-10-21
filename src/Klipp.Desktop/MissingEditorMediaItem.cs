namespace Klipp.Desktop;

public sealed class MissingEditorMediaItem
{
    public string OriginalPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsAudio { get; set; }
    public bool IsGraphic { get; set; }
    public double ExpectedDurationSeconds { get; set; }
    public string KindLabel => IsAudio ? "Audio" : IsGraphic ? "Graphic" : "Video";
}

public sealed class MissingMediaRelinkEventArgs : System.EventArgs
{
    public MissingMediaRelinkEventArgs(string originalPath, bool isAudio, bool isGraphic = false)
    {
        OriginalPath = originalPath;
        IsAudio = isAudio;
        IsGraphic = isGraphic;
    }

    public string OriginalPath { get; }
    public bool IsAudio { get; }
    public bool IsGraphic { get; }
}
