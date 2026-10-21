namespace Klipp.Core.Editing;

public readonly record struct EditorExportPresetSettings(
    string ResolutionMode, int Width, int Height, string Quality,
    int FrameRate, int VideoBitrateMbps, int AudioBitrateKbps);

public sealed record EditorExportPreset(
    string Id, string Name, string Description, string ResolutionMode, string Quality,
    int FrameRate, int VideoBitrateMbps, int AudioBitrateKbps);

/// <summary>Export choices preserve the canvas aspect ratio and leave the edit unchanged.</summary>
public static class EditorExportPresetMath
{
    public static IReadOnlyList<EditorExportPreset> Presets { get; } = Array.AsReadOnly<EditorExportPreset>(
    [
        new("balanced", "Balanced · canvas size", "Your canvas size, 60 fps and balanced file size.",
            "canvas", "high", 60, 16, 192),
        new("sharing", "1080p · sharing", "1080p at 60 fps, keeping your current framing.",
            "1080p", "high", 60, 16, 192),
        new("small", "720p · smaller file", "720p at 30 fps for a smaller file, keeping your framing.",
            "720p", "high", 30, 4, 128),
        new("quality", "High quality · canvas size", "Your canvas size with more detail and a higher bitrate.",
            "canvas", "max", 60, 32, 256)
    ]);

    public static EditorExportPresetSettings Resolve(EditorExportPreset preset, int canvasWidth, int canvasHeight)
    {
        var (width, height) = ResolveDimensions(preset.ResolutionMode, canvasWidth, canvasHeight);
        return new(preset.ResolutionMode, width, height, preset.Quality,
            preset.FrameRate, preset.VideoBitrateMbps, preset.AudioBitrateKbps);
    }

    public static string Match(EditorExportPresetSettings settings, int canvasWidth, int canvasHeight)
    {
        foreach (var preset in Presets)
            if (settings == Resolve(preset, canvasWidth, canvasHeight)) return preset.Id;
        return "custom";
    }

    public static (int Width, int Height) ResolveDimensions(string mode, int canvasWidth, int canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) (canvasWidth, canvasHeight) = (1280, 720);
        if (mode is not ("720p" or "1080p" or "1440p" or "2160p"))
            return (canvasWidth, canvasHeight);
        var shortEdge = mode switch { "720p" => 720, "1440p" => 1440, "2160p" => 2160, _ => 1080 };
        var aspect = canvasWidth / (double)canvasHeight;
        return aspect >= 1
            ? (EvenDimension(shortEdge * aspect, 320), shortEdge)
            : (shortEdge, EvenDimension(shortEdge / aspect, 180));
    }

    /// <summary>Approximate ABR video plus the final mixed AAC stream and MP4 overhead.</summary>
    public static long EstimateBytes(double durationSeconds, int videoBitrateMbps, int audioBitrateKbps,
        bool includeAudio = true)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return 0;
        var bitsPerSecond = Math.Clamp(videoBitrateMbps, 2, 120) * 1_000_000d +
            (includeAudio ? Math.Clamp(audioBitrateKbps, 64, 320) * 1000d : 0);
        var bytes = durationSeconds * bitsPerSecond / 8 * 1.02;
        return bytes >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(bytes);
    }

    private static int EvenDimension(double value, int minimum) =>
        (int)Math.Clamp(Math.Round(value / 2) * 2, minimum, 7680);
}
