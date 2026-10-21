using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Klipp.Desktop.Services;

internal static class FFmpegProgressReader
{
    public static void AddArguments(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add("-nostats");
    }

    public static async Task ReadAsync(
        StreamReader reader,
        double durationSeconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var durationMicroseconds = Math.Max(1, durationSeconds * 1_000_000.0);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Equals("progress=end", StringComparison.Ordinal))
            {
                progress.Report(1);
                continue;
            }

            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                !long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var elapsedMicroseconds))
            {
                continue;
            }

            progress.Report(Math.Clamp(elapsedMicroseconds / durationMicroseconds, 0, 0.995));
        }
    }

    public static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Processen kan redan ha avslutats mellan kontrollen och Kill.
        }
    }
}
