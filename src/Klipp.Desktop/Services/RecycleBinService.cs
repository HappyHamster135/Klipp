using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;

namespace Klipp.Desktop.Services;

public sealed class RecycleBinService
{
    public async Task MoveFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The clip no longer exists.", fullPath);

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystem.DeleteFile(
                fullPath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
        }, cancellationToken).ConfigureAwait(false);

        if (File.Exists(fullPath))
            throw new IOException("Windows did not move the clip to the Recycle Bin.");
    }
}
