using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

public sealed record FFmpegEncoderCapability(
    string Profile,
    string Codec,
    string DisplayName,
    bool IsHardware,
    bool IsAvailable);

public sealed record FFmpegEncoderChoice(
    string RequestedProfile,
    string Codec,
    string DisplayName,
    bool IsHardware,
    bool UsedFallback);

public sealed class FFmpegEncoderService
{
    public const string SoftwareCodec = "libx264";

    private static readonly (string Profile, string Codec, string DisplayName, bool IsHardware)[] KnownEncoders =
    [
        ("nvenc", "h264_nvenc", "NVIDIA NVENC", true),
        ("qsv", "h264_qsv", "Intel Quick Sync", true),
        ("amf", "h264_amf", "AMD AMF", true),
        ("software", SoftwareCodec, "CPU (x264)", false)
    ];

    private static readonly SemaphoreSlim DetectionGate = new(1, 1);
    private static IReadOnlyList<FFmpegEncoderCapability>? _cachedCapabilities;
    private readonly FFmpegLocator _locator;

    public FFmpegEncoderService(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
    }

    public async Task<IReadOnlyList<FFmpegEncoderCapability>> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cachedCapabilities is not null)
            return _cachedCapabilities;

        await DetectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedCapabilities is not null)
                return _cachedCapabilities;

            var ffmpegPath = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var capabilities = new List<FFmpegEncoderCapability>(KnownEncoders.Length);
            foreach (var encoder in KnownEncoders)
            {
                var available = await ProbeEncoderAsync(ffmpegPath, encoder.Codec, cancellationToken)
                    .ConfigureAwait(false);
                capabilities.Add(new FFmpegEncoderCapability(
                    encoder.Profile,
                    encoder.Codec,
                    encoder.DisplayName,
                    encoder.IsHardware,
                    available));
            }

            _cachedCapabilities = capabilities;
            return _cachedCapabilities;
        }
        finally
        {
            DetectionGate.Release();
        }
    }

    public async Task<FFmpegEncoderChoice> ResolveAsync(
        string? requestedProfile,
        CancellationToken cancellationToken = default)
    {
        var capabilities = await DetectAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(requestedProfile, capabilities);
    }

    public static FFmpegEncoderChoice Resolve(
        string? requestedProfile,
        IReadOnlyList<FFmpegEncoderCapability> capabilities)
    {
        var normalizedProfile = NormalizeProfile(requestedProfile);
        FFmpegEncoderCapability? selected = null;

        if (normalizedProfile == "auto")
        {
            selected = capabilities.FirstOrDefault(capability => capability.IsHardware && capability.IsAvailable)
                ?? capabilities.FirstOrDefault(capability =>
                    capability.Profile == "software" && capability.IsAvailable);
        }
        else
        {
            selected = capabilities.FirstOrDefault(capability =>
                capability.Profile == normalizedProfile && capability.IsAvailable);
        }

        var usedFallback = selected is null;
        selected ??= capabilities.FirstOrDefault(capability =>
            capability.Profile == "software" && capability.IsAvailable);
        selected ??= new FFmpegEncoderCapability(
            "software",
            SoftwareCodec,
            "CPU (x264)",
            IsHardware: false,
            IsAvailable: true);

        return new FFmpegEncoderChoice(
            normalizedProfile,
            selected.Codec,
            selected.DisplayName,
            selected.IsHardware,
            usedFallback);
    }

    public static string NormalizeProfile(string? profile) => profile?.Trim().ToLowerInvariant() switch
    {
        "software" => "software",
        "nvenc" => "nvenc",
        "qsv" => "qsv",
        "amf" => "amf",
        _ => "auto"
    };

    private static async Task<bool> ProbeEncoderAsync(
        string ffmpegPath,
        string codec,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("color=c=black:s=64x64:r=1");
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add(codec);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi);
        if (process is null)
            return false;

        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0;
    }
}

public static class FFmpegVideoEncoding
{
    public static void AddArguments(
        ProcessStartInfo psi,
        string? encoder,
        int bitrateMbps,
        string? quality,
        int fallbackCrf = 20)
    {
        var codec = NormalizeCodec(encoder);
        var normalizedQuality = quality?.Trim().ToLowerInvariant() switch
        {
            "draft" or "max" => quality.Trim().ToLowerInvariant(),
            _ => "high"
        };
        var bitrateKbps = Math.Clamp(bitrateMbps, 2, 120) * 1000;

        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add(codec);

        switch (codec)
        {
            case "h264_nvenc":
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add(normalizedQuality switch
                {
                    "draft" => "p1",
                    "max" => "p7",
                    _ => "p4"
                });
                break;
            case "h264_qsv":
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add(normalizedQuality switch
                {
                    "draft" => "veryfast",
                    "max" => "veryslow",
                    _ => "medium"
                });
                break;
            case "h264_amf":
                psi.ArgumentList.Add("-quality");
                psi.ArgumentList.Add(normalizedQuality switch
                {
                    "draft" => "speed",
                    "max" => "quality",
                    _ => "balanced"
                });
                break;
            default:
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add(normalizedQuality switch
                {
                    "draft" => "ultrafast",
                    "max" => "medium",
                    _ => "veryfast"
                });
                break;
        }

        if (bitrateMbps > 0)
        {
            psi.ArgumentList.Add("-b:v");
            psi.ArgumentList.Add($"{bitrateKbps}k");
            psi.ArgumentList.Add("-maxrate");
            psi.ArgumentList.Add($"{(int)Math.Round(bitrateKbps * 1.35)}k");
            psi.ArgumentList.Add("-bufsize");
            psi.ArgumentList.Add($"{bitrateKbps * 2}k");
        }
        else if (codec == FFmpegEncoderService.SoftwareCodec)
        {
            psi.ArgumentList.Add("-crf");
            psi.ArgumentList.Add(Math.Clamp(fallbackCrf, 12, 35).ToString(CultureInfo.InvariantCulture));
        }

        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
    }

    public static string NormalizeCodec(string? encoder) => encoder?.Trim().ToLowerInvariant() switch
    {
        "h264_nvenc" => "h264_nvenc",
        "h264_qsv" => "h264_qsv",
        "h264_amf" => "h264_amf",
        _ => FFmpegEncoderService.SoftwareCodec
    };
}
