using System;
using System.Text.Json.Serialization;

namespace Klipp.Desktop.Models;

public enum EditorExportJobStatus
{
    Pending,
    Rendering,
    Completed,
    Failed,
    Cancelled
}

public sealed class EditorExportJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectName { get; set; } = "Untitled";
    public string ProjectSnapshotPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public EditorExportJobStatus Status { get; set; } = EditorExportJobStatus.Pending;
    public double Progress { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    [JsonIgnore]
    public bool CanCancel => Status is EditorExportJobStatus.Pending or EditorExportJobStatus.Rendering;

    [JsonIgnore]
    public bool CanRetry => Status is EditorExportJobStatus.Failed or EditorExportJobStatus.Cancelled;

    [JsonIgnore]
    public bool CanOpen => Status == EditorExportJobStatus.Completed && System.IO.File.Exists(OutputPath);

    public EditorExportJob Copy() => new()
    {
        Id = Id,
        ProjectName = ProjectName,
        ProjectSnapshotPath = ProjectSnapshotPath,
        OutputPath = OutputPath,
        Status = Status,
        Progress = Progress,
        ErrorMessage = ErrorMessage,
        Attempt = Attempt,
        CreatedUtc = CreatedUtc,
        StartedUtc = StartedUtc,
        CompletedUtc = CompletedUtc
    };
}

public sealed class EditorExportJobEventArgs : EventArgs
{
    public EditorExportJobEventArgs(EditorExportJob job)
    {
        Job = job;
    }

    public EditorExportJob Job { get; }
}
