namespace Klipp.Core.Editing;

public readonly record struct EditorEffectPoint(double X, double Y);

public readonly record struct EditorEffectRegion(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>Source-frame region interpolation and inward-only effect coverage.</summary>
public static class EditorEffectRegionMath
{
    public const double MinimumSize = 0.02;

    public static EditorEffectPoint SourceToHost(EditorSourceFit fit, EditorEffectPoint point) => new(
        (fit.SourceLeft + point.X * fit.SourceWidth) * fit.RefitScale + fit.TranslateX,
        (fit.SourceTop + point.Y * fit.SourceHeight) * fit.RefitScale + fit.TranslateY);

    // Pointer positions stay unclamped so off-frame motion keeps its grab offset.
    public static EditorEffectPoint HostToSource(EditorSourceFit fit, EditorEffectPoint point) => new(
        ((point.X - fit.TranslateX) / fit.RefitScale - fit.SourceLeft) / fit.SourceWidth,
        ((point.Y - fit.TranslateY) / fit.RefitScale - fit.SourceTop) / fit.SourceHeight);

    public static EditorEffectRegion Normalize(EditorEffectRegion region, double minimumSize = MinimumSize)
    {
        var minimum = ValidMinimum(minimumSize);
        var width = Math.Clamp(Finite(region.Width, 1), minimum, 1);
        var height = Math.Clamp(Finite(region.Height, 1), minimum, 1);
        return new(Math.Clamp(Finite(region.X, 0), 0, 1 - width),
            Math.Clamp(Finite(region.Y, 0), 0, 1 - height), width, height);
    }

    public static EditorEffectRegion MoveWithinSource(EditorEffectRegion region, EditorEffectPoint delta)
    {
        region = Normalize(region);
        return region with
        {
            X = Math.Clamp(region.X + Finite(delta.X, 0), 0, 1 - region.Width),
            Y = Math.Clamp(region.Y + Finite(delta.Y, 0), 0, 1 - region.Height)
        };
    }

    /// <summary>Keeps the opposite corner fixed; point includes the caller's initial grab-offset correction.</summary>
    public static EditorEffectRegion ResizeCorner(EditorEffectRegion region, EditorEffectPoint point,
        string corner, double minimumSize = MinimumSize)
    {
        var minimum = ValidMinimum(minimumSize);
        region = Normalize(region, minimum);
        var left = region.X;
        var top = region.Y;
        var right = region.Right;
        var bottom = region.Bottom;
        switch (corner)
        {
            case "nw":
                left = Math.Clamp(Finite(point.X, left), 0, right - minimum);
                top = Math.Clamp(Finite(point.Y, top), 0, bottom - minimum);
                break;
            case "ne":
                right = Math.Clamp(Finite(point.X, right), left + minimum, 1);
                top = Math.Clamp(Finite(point.Y, top), 0, bottom - minimum);
                break;
            case "sw":
                left = Math.Clamp(Finite(point.X, left), 0, right - minimum);
                bottom = Math.Clamp(Finite(point.Y, bottom), top + minimum, 1);
                break;
            case "se":
                right = Math.Clamp(Finite(point.X, right), left + minimum, 1);
                bottom = Math.Clamp(Finite(point.Y, bottom), top + minimum, 1);
                break;
            default:
                return region;
        }
        return new(left, top, right - left, bottom - top);
    }

    public static EditorEffectRegion Interpolate(EditorEffectRegion from, EditorEffectRegion to,
        double progress, string easing = "linear")
    {
        from = Normalize(from);
        to = Normalize(to);
        progress = Ease(progress, easing);
        return Normalize(new(from.X + (to.X - from.X) * progress, from.Y + (to.Y - from.Y) * progress,
            from.Width + (to.Width - from.Width) * progress, from.Height + (to.Height - from.Height) * progress));
    }

    public static double Ease(double progress, string easing)
    {
        progress = Math.Clamp(Finite(progress, 0), 0, 1);
        return easing switch
        {
            "ease-in" => progress * progress,
            "ease-out" => 1 - (1 - progress) * (1 - progress),
            "ease-in-out" => progress < .5 ? 2 * progress * progress : 1 - Math.Pow(-2 * progress + 2, 2) / 2,
            "hold" => progress < 1 ? 0 : 1,
            _ => progress
        };
    }

    /// <summary>Pixel coordinates are source-frame sample centers, typically x + .5 and y + .5.</summary>
    public static double Coverage(EditorEffectRegion region, string shape, double feather,
        double sourceWidth, double sourceHeight, double pixelX, double pixelY)
    {
        if (!double.IsFinite(sourceWidth) || !double.IsFinite(sourceHeight) || sourceWidth <= 0 || sourceHeight <= 0 ||
            !double.IsFinite(pixelX) || !double.IsFinite(pixelY)) return 0;
        region = Normalize(region);
        feather = Math.Clamp(Finite(feather, 0), 0, 1);
        var left = region.X * sourceWidth;
        var top = region.Y * sourceHeight;
        var width = region.Width * sourceWidth;
        var height = region.Height * sourceHeight;
        if (shape == "ellipse")
        {
            var x = (pixelX - left - width / 2) / (width / 2);
            var y = (pixelY - top - height / 2) / (height / 2);
            var radial = Math.Sqrt(x * x + y * y);
            if (radial >= 1) return 0;
            return feather <= 0 ? 1 : Math.Clamp((1 - radial) / feather, 0, 1);
        }
        var inward = Math.Min(Math.Min(pixelX - left, left + width - pixelX),
            Math.Min(pixelY - top, top + height - pixelY));
        if (inward < 0 || pixelX >= left + width || pixelY >= top + height) return 0;
        return feather <= 0 ? 1 : Math.Clamp(inward / (Math.Min(width, height) * .5 * feather), 0, 1);
    }

    private static double ValidMinimum(double value) => Math.Clamp(Finite(value, MinimumSize), .000001, 1);
    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
