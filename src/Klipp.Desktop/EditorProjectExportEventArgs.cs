using System;
using Klipp.Desktop.Models;

namespace Klipp.Desktop;

public sealed class EditorProjectExportEventArgs : EventArgs
{
    public EditorProjectExportEventArgs(EditorProject project)
    {
        Project = project;
    }

    public EditorProject Project { get; }
}
