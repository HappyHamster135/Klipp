using System;

namespace Klipp.Desktop.Models;

public sealed class SavedEditorProjectInfo
{
    public string ProjectPath { get; set; } = string.Empty;
    public string Name { get; set; } = "Untitled";
    public DateTime UpdatedUtc { get; set; }
    public int ClipCount { get; set; }

    public string Summary => $"{ClipCount} clips - {UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
}
