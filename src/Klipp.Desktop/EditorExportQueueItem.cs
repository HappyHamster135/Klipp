using System;
using System.IO;
using Klipp.Desktop.Models;
using Microsoft.UI.Xaml;

namespace Klipp.Desktop;

public sealed class EditorExportQueueItem
{
    public string Id { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string OutputName { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string DetailText { get; set; } = string.Empty;
    public double ProgressPercent { get; set; }
    public Visibility ProgressVisibility { get; set; }
    public Visibility CancelVisibility { get; set; }
    public Visibility RetryVisibility { get; set; }
    public Visibility OpenVisibility { get; set; }

    public static EditorExportQueueItem FromJob(EditorExportJob job)
    {
        var progressPercent = Math.Clamp(job.Progress * 100, 0, 100);
        var statusText = job.Status switch
        {
            EditorExportJobStatus.Pending => "Queued",
            EditorExportJobStatus.Rendering => $"Rendering {progressPercent:0}%",
            EditorExportJobStatus.Completed => "Completed",
            EditorExportJobStatus.Failed => "Failed",
            EditorExportJobStatus.Cancelled => "Cancelled",
            _ => job.Status.ToString()
        };
        var detail = job.Status == EditorExportJobStatus.Failed && !string.IsNullOrWhiteSpace(job.ErrorMessage)
            ? job.ErrorMessage
            : job.CompletedUtc?.ToLocalTime().ToString("g") ?? Path.GetFileName(job.OutputPath);

        return new EditorExportQueueItem
        {
            Id = job.Id,
            ProjectName = job.ProjectName,
            OutputName = Path.GetFileName(job.OutputPath),
            StatusText = statusText,
            DetailText = detail,
            ProgressPercent = progressPercent,
            ProgressVisibility = job.Status is EditorExportJobStatus.Pending or EditorExportJobStatus.Rendering
                ? Visibility.Visible
                : Visibility.Collapsed,
            CancelVisibility = job.CanCancel ? Visibility.Visible : Visibility.Collapsed,
            RetryVisibility = job.CanRetry ? Visibility.Visible : Visibility.Collapsed,
            OpenVisibility = job.CanOpen ? Visibility.Visible : Visibility.Collapsed
        };
    }
}

public enum EditorExportJobAction
{
    Cancel,
    Retry,
    Open
}

public sealed class EditorExportJobActionEventArgs : EventArgs
{
    public EditorExportJobActionEventArgs(string jobId, EditorExportJobAction action)
    {
        JobId = jobId;
        Action = action;
    }

    public string JobId { get; }
    public EditorExportJobAction Action { get; }
}
