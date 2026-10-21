using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

public sealed record AudioLoudnessAnalysis(
    double IntegratedLoudnessLufs,
    double TruePeakDb,
    double GainDb,
    double TargetLoudnessLufs,
    double TargetTruePeakDb);

public sealed class AudioLoudnessAnalysisService
{
    public const double DefaultTargetLoudnessLufs = -14;
    public const double DefaultTargetTruePeakDb = -1.5;

    private readonly FFmpegLocator _locator;

    public AudioLoudnessAnalysisService(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
    }

    public async Task<AudioLoudnessAnalysis?> AnalyzeAsync(
        string sourcePath,
        double trimStartSeconds,
        double trimEndSeconds,
        CancellationToken cancellationToken = default, int audioStreamIndex = 0)
    {
        if (!File.Exists(sourcePath))
            return null;

        var duration = Math.Max(0.1, trimEndSeconds - trimStartSeconds);
        var ffmpegPath = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostats");
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(trimStartSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(duration.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add($"0:a:{Math.Max(0, audioStreamIndex)}");
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-dn");
        startInfo.ArgumentList.Add("-af");
        startInfo.ArgumentList.Add(
            $"loudnorm=I={DefaultTargetLoudnessLufs.ToString(CultureInfo.InvariantCulture)}:" +
            $"LRA=11:TP={DefaultTargetTruePeakDb.ToString(CultureInfo.InvariantCulture)}:print_format=json");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("-");

        using var process = Process.Start(startInfo);
        if (process is null)
            return null;

        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        });

        try
        {
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await standardOutputTask.ConfigureAwait(false);
            var error = await standardErrorTask.ConfigureAwait(false);
            return TryParseAnalysis(error + Environment.NewLine + output, out var analysis)
                ? analysis
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryParseAnalysis(string output, out AudioLoudnessAnalysis analysis)
    {
        analysis = new AudioLoudnessAnalysis(0, 0, 0, DefaultTargetLoudnessLufs, DefaultTargetTruePeakDb);
        if (string.IsNullOrWhiteSpace(output))
            return false;

        var end = output.LastIndexOf('}');
        var start = end < 0 ? -1 : output.LastIndexOf('{', end);
        if (start < 0 || end <= start)
            return false;

        try
        {
            using var document = JsonDocument.Parse(output[start..(end + 1)]);
            if (!TryReadNumber(document.RootElement, "input_i", out var integratedLoudness) ||
                !TryReadNumber(document.RootElement, "input_tp", out var truePeak))
            {
                return false;
            }

            var loudnessGain = DefaultTargetLoudnessLufs - integratedLoudness;
            var peakSafeGain = DefaultTargetTruePeakDb - truePeak;
            var gain = Math.Clamp(Math.Min(loudnessGain, peakSafeGain), -24, 12);
            analysis = new AudioLoudnessAnalysis(
                integratedLoudness,
                truePeak,
                gain,
                DefaultTargetLoudnessLufs,
                DefaultTargetTruePeakDb);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadNumber(JsonElement root, string propertyName, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
            return double.IsFinite(value);
        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }
}
