namespace Klipp.Core.Editing;

public readonly record struct EditorSourceFit(
    double SourceLeft, double SourceTop, double SourceWidth, double SourceHeight,
    double CropLeft, double CropTop, double CropWidth, double CropHeight,
    double RefitScale, double TranslateX, double TranslateY);

/// <summary>Fits a source crop uniformly while keeping its geometry in original canvas coordinates.</summary>
public static class EditorSourceFitMath
{
    public static EditorSourceFit Calculate(double canvasWidth, double canvasHeight,
        double sourceWidth, double sourceHeight,
        double cropLeft, double cropTop, double cropRight, double cropBottom)
    {
        canvasWidth = ValidDimension(canvasWidth) ? canvasWidth : 1;
        canvasHeight = ValidDimension(canvasHeight) ? canvasHeight : 1;
        if (!ValidDimension(sourceWidth) || !ValidDimension(sourceHeight))
        {
            sourceWidth = canvasWidth;
            sourceHeight = canvasHeight;
        }

        var uniformScale = Math.Min(canvasWidth / sourceWidth, canvasHeight / sourceHeight);
        var width = sourceWidth * uniformScale;
        var height = sourceHeight * uniformScale;
        var left = (canvasWidth - width) / 2;
        var top = (canvasHeight - height) / 2;

        const double minimumRetainedFraction = 1e-6;
        cropLeft = CropFraction(cropLeft, 1 - minimumRetainedFraction);
        cropTop = CropFraction(cropTop, 1 - minimumRetainedFraction);
        cropRight = CropFraction(cropRight, Math.Max(0, 1 - cropLeft - minimumRetainedFraction));
        cropBottom = CropFraction(cropBottom, Math.Max(0, 1 - cropTop - minimumRetainedFraction));
        var cropX = left + width * cropLeft;
        var cropY = top + height * cropTop;
        var cropWidth = width * (1 - cropLeft - cropRight);
        var cropHeight = height * (1 - cropTop - cropBottom);
        if (cropLeft == 0 && cropTop == 0 && cropRight == 0 && cropBottom == 0)
            return new(left, top, width, height, cropX, cropY, cropWidth, cropHeight, 1, 0, 0);

        var refit = Math.Min(canvasWidth / cropWidth, canvasHeight / cropHeight);
        return new(left, top, width, height, cropX, cropY, cropWidth, cropHeight, refit,
            canvasWidth / 2 - (cropX + cropWidth / 2) * refit,
            canvasHeight / 2 - (cropY + cropHeight / 2) * refit);
    }

    private static bool ValidDimension(double value) => double.IsFinite(value) && value > 0;
    private static double CropFraction(double value, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, maximum) : 0;
}
