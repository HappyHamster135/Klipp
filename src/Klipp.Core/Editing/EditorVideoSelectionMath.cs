namespace Klipp.Core.Editing;

public readonly record struct EditorVideoSelectionBounds(double Left, double Top, double Width, double Height);

/// <summary>The visible source rectangle, excluding Uniform-fit letterboxing and discarded crop pixels.</summary>
public static class EditorVideoSelectionMath
{
    public static EditorVideoSelectionBounds GetSourceBounds(EditorSourceFit fit) => new(
        fit.CropLeft * fit.RefitScale + fit.TranslateX,
        fit.CropTop * fit.RefitScale + fit.TranslateY,
        fit.CropWidth * fit.RefitScale,
        fit.CropHeight * fit.RefitScale);

    /// <summary>Place the full source image before the retained crop clips it, without a second aspect fit.</summary>
    public static EditorVideoSelectionBounds GetSourceImageBounds(EditorSourceFit fit) => new(
        fit.SourceLeft * fit.RefitScale + fit.TranslateX,
        fit.SourceTop * fit.RefitScale + fit.TranslateY,
        fit.SourceWidth * fit.RefitScale,
        fit.SourceHeight * fit.RefitScale);
}
