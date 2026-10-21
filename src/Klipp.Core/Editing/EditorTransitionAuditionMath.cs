namespace Klipp.Core.Editing;

/// <summary>Normalized geometry for the editor's isolated A/B transition sample.</summary>
public readonly record struct EditorTransitionAuditionFrame(
    double OutgoingOpacity = 1,
    double IncomingOpacity = 1,
    double WhiteOpacity = 0,
    double RevealX = 0,
    double RevealY = 0,
    double RevealWidth = 1,
    double RevealHeight = 1,
    double OutgoingX = 0,
    double OutgoingY = 0,
    double IncomingX = 0,
    double IncomingY = 0,
    string MaskKind = "rectangle",
    double MaskProgress = 1);

public static class EditorTransitionAuditionMath
{
    public const double HoldSeconds = 0.35;

    public static double CycleSeconds(double durationSeconds) =>
        Math.Clamp(double.IsFinite(durationSeconds) ? durationSeconds : 0.5, 0.1, 2) + HoldSeconds * 2;

    public static double ProgressAt(double elapsedSeconds, double durationSeconds) =>
        Math.Clamp((elapsedSeconds - HoldSeconds) / (CycleSeconds(durationSeconds) - HoldSeconds * 2), 0, 1);

    public static EditorTransitionAuditionFrame Evaluate(string style, double progress)
    {
        var p = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        // Keep a full-size reveal for every style except wipes. Movement is a
        // transform inside that reveal, matching the editor's live compositor.
        var frame = new EditorTransitionAuditionFrame(OutgoingOpacity: 1);
        return style switch
        {
            "cut" => frame with { IncomingOpacity = p < 0.5 ? 0 : 1 },
            "fadeblack" => frame with
            {
                OutgoingOpacity = p < 0.5 ? 1 - p * 2 : 0,
                IncomingOpacity = p < 0.5 ? 0 : (p - 0.5) * 2
            },
            "fadewhite" => frame with
            {
                OutgoingOpacity = p < 0.5 ? 1 : 0,
                IncomingOpacity = p < 0.5 ? 0 : 1,
                WhiteOpacity = p < 0.5 ? p * 2 : (1 - p) * 2
            },
            "wipeleft" => frame with { RevealX = 1 - p, RevealWidth = p },
            "wiperight" => frame with { RevealWidth = p },
            "wipeup" => frame with { RevealY = 1 - p, RevealHeight = p },
            "wipedown" => frame with { RevealHeight = p },
            "rectcrop" => frame with { RevealX = (1 - p) / 2, RevealY = (1 - p) / 2, RevealWidth = p, RevealHeight = p, IncomingOpacity = p > 0 ? 1 : 0 },
            "horzopen" => frame with { RevealX = (1 - p) / 2, RevealWidth = p, IncomingOpacity = p > 0 ? 1 : 0 },
            "vertopen" => frame with { RevealY = (1 - p) / 2, RevealHeight = p, IncomingOpacity = p > 0 ? 1 : 0 },
            "circleopen" => frame with { MaskKind = "circle", MaskProgress = p, IncomingOpacity = p > 0 ? 1 : 0 },
            "diagtl" => frame with { MaskKind = "diagonal", MaskProgress = p, IncomingOpacity = p > 0 ? 1 : 0 },
            "slideleft" => frame with { OutgoingX = -p, IncomingX = 1 - p },
            "slideright" => frame with { OutgoingX = p, IncomingX = p - 1 },
            "slideup" => frame with { OutgoingY = -p, IncomingY = 1 - p },
            "slidedown" => frame with { OutgoingY = p, IncomingY = p - 1 },
            _ => frame with { IncomingOpacity = p }
        };
    }

    /// <summary>The diagonal reveal's normalized polygon, clipped to the picture.</summary>
    public static IReadOnlyList<(double X, double Y)> GetRevealPolygon(double progress)
    {
        var p = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        var edge = 2 * p;
        return edge <= 1
            ? [(0, 0), (edge, 0), (0, edge)]
            : [(0, 0), (1, 0), (1, edge - 1), (edge - 1, 1), (0, 1)];
    }

    public static double GetCircleRadius(double width, double height, double progress) =>
        Math.Clamp(double.IsFinite(progress) ? progress : 0, 0, 1) * Math.Sqrt(width * width + height * height) / 2;

    /// <summary>Pixel-center coverage shared by shape validation and renderer tests.</summary>
    public static bool IsIncomingVisible(EditorTransitionAuditionFrame frame, double x, double y, double width, double height)
    {
        if (width <= 0 || height <= 0 || frame.IncomingOpacity <= 0) return false;
        if (frame.MaskKind == "circle")
        {
            var radius = GetCircleRadius(width, height, frame.MaskProgress);
            return Math.Pow(x - width / 2, 2) + Math.Pow(y - height / 2, 2) <= radius * radius;
        }
        if (frame.MaskKind == "diagonal")
            return x / width + y / height <= 2 * frame.MaskProgress;
        return x >= frame.RevealX * width && y >= frame.RevealY * height &&
            x <= (frame.RevealX + frame.RevealWidth) * width &&
            y <= (frame.RevealY + frame.RevealHeight) * height;
    }
}
