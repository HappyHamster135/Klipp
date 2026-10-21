using System;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Desktop.Services;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    /// <summary>Finishes autosave before an explicitly requested update restart.</summary>
    public async Task FlushProjectForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Save the editor project on its UI thread before restarting.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        _projectChangeTimer.Stop();
        CommitPendingProjectChange();

        try
        {
            // Stop() and the debounce callback may already be saving. Let their
            // queued snapshots finish before writing the final current state.
            while (_projectSaveInProgress)
                await Task.Delay(25, token);

            _projectSaveInProgress = true;
            try
            {
                do
                {
                    token.ThrowIfCancellationRequested();
                    _projectChangeTimer.Stop();
                    _projectSaveQueued = false;
                    CommitPendingProjectChange();
                    var document = _project;
                    var sourcePath = _projectSourcePath;
                    if (document is null || string.IsNullOrWhiteSpace(sourcePath)) return;

                    // Unlike background autosave, a failed final save must reach
                    // the caller so it can keep Klipp open and abandon restart.
                    await _projectService.SaveAutosaveAsync(EditorProjectService.Clone(document), sourcePath, token);
                    if (ReferenceEquals(_project, document))
                        ProjectStatusText.Text = $"Saved {DateTime.Now:HH:mm:ss}";
                } while (_projectSaveQueued);
            }
            finally
            {
                _projectSaveInProgress = false;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The project is still saving. Wait a moment and try restarting again.", exception);
        }
    }
}
