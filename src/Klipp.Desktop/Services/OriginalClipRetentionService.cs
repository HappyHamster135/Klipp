using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Models;

namespace Klipp.Desktop.Services;

public sealed record OriginalClipRetentionResult(
    IReadOnlyList<string> MovedPaths,
    long ReclaimedBytes,
    int FailedFiles,
    int ChangedFiles,
    int MissingFiles);

public sealed class OriginalClipRetentionService
{
    private readonly EditorProjectService _projectService;
    private readonly RecycleBinService _recycleBinService;

    public OriginalClipRetentionService(
        EditorProjectService projectService,
        RecycleBinService recycleBinService)
    {
        _projectService = projectService;
        _recycleBinService = recycleBinService;
    }

    public async Task<ClipRetentionPlan> InspectAsync(
        IEnumerable<ClipViewModel> clips,
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var clipSnapshot = clips.ToList();
        var referencedPaths = await _projectService
            .GetReferencedMediaPathsAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = new List<ClipRetentionItem>(clipSnapshot.Count);
        foreach (var clip in clipSnapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(clip.FilePath))
                continue;

            FileInfo file;
            try
            {
                file = new FileInfo(clip.FilePath);
                if (!file.Exists)
                    continue;
            }
            catch
            {
                continue;
            }

            items.Add(new ClipRetentionItem(
                file.FullName,
                file.LastWriteTimeUtc,
                file.Length,
                clip.IsFavorite,
                referencedPaths.Contains(file.FullName)));
        }

        return OriginalClipRetentionPolicy.CreatePlan(
            items,
            DateTime.UtcNow,
            retentionDays);
    }

    public async Task<OriginalClipRetentionResult> ExecuteAsync(
        ClipRetentionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var movedPaths = new List<string>();
        long reclaimedBytes = 0;
        var failedFiles = 0;
        var changedFiles = 0;
        var missingFiles = 0;

        foreach (var candidate in plan.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo currentFile;
            try
            {
                currentFile = new FileInfo(candidate.FilePath);
                if (!currentFile.Exists)
                {
                    missingFiles++;
                    continue;
                }

                var writeTimeChanged = Math.Abs(
                    (currentFile.LastWriteTimeUtc - candidate.LastWriteUtc).TotalSeconds) > 2;
                if (writeTimeChanged || currentFile.Length != candidate.SizeBytes)
                {
                    changedFiles++;
                    continue;
                }

                await _recycleBinService
                    .MoveFileAsync(currentFile.FullName, cancellationToken)
                    .ConfigureAwait(false);
                movedPaths.Add(currentFile.FullName);
                reclaimedBytes += Math.Max(0, candidate.SizeBytes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failedFiles++;
            }
        }

        return new OriginalClipRetentionResult(
            movedPaths,
            reclaimedBytes,
            failedFiles,
            changedFiles,
            missingFiles);
    }
}
