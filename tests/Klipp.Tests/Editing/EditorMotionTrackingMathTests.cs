using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorMotionTrackingMathTests
{
    private const int Width = 120;
    private const int Height = 80;

    [Fact]
    public void TracksTranslationOfTexturedRegionAcrossMultipleFrames()
    {
        var tracker = new EditorMotionTemplateTracker(Frame(20, 25), Width, Height,
            new(20.0 / Width, 25.0 / Height, 18.0 / Width, 16.0 / Height));
        for (var index = 1; index <= 6; index++)
        {
            Assert.True(tracker.TryTrack(Frame(20 + index * 3, 25 + index), out var match));
            Assert.Equal((29.0 + index * 3) / Width, match.CenterX, 8);
            Assert.Equal((33.0 + index) / Height, match.CenterY, 8);
            Assert.True(match.Confidence > 0.99);
        }
    }

    [Fact]
    public void CorrelationToleratesUniformLightingChange()
    {
        var tracker = new EditorMotionTemplateTracker(Frame(20, 25), Width, Height,
            new(20.0 / Width, 25.0 / Height, 18.0 / Width, 16.0 / Height));
        Assert.True(tracker.TryTrack(Frame(27, 28, 15), out var match));
        Assert.Equal(36.0 / Width, match.CenterX, 8);
        Assert.Equal(36.0 / Height, match.CenterY, 8);
    }

    [Fact]
    public void StopsWhenSubjectDisappearsRatherThanInventingMotion()
    {
        var tracker = new EditorMotionTemplateTracker(Frame(20, 25), Width, Height,
            new(20.0 / Width, 25.0 / Height, 18.0 / Width, 16.0 / Height));
        Assert.False(tracker.TryTrack(Enumerable.Repeat((byte)70, Width * Height).ToArray(), out var match));
        Assert.Equal(tracker.InitialMatch.CenterX, match.CenterX);
    }

    [Fact]
    public void RejectsAFeaturelessReference()
    {
        Assert.Throws<ArgumentException>(() => new EditorMotionTemplateTracker(new byte[Width * Height],
            Width, Height, new(0.2, 0.2, 0.2, 0.2)));
    }

    [Fact]
    public void PanConversionExactlyCancelsTrackedDisplacement()
    {
        const double displacement = 0.07;
        const double zoom = 1.5;
        const double fraction = 0.75;
        var pan = EditorMotionTrackingMath.PanForDisplacement(displacement, zoom, fraction);
        var apparentMovement = zoom * displacement * fraction + (1 - zoom) * pan / 2;
        Assert.Equal(0, apparentMovement, 8);
    }

    [Theory]
    [InlineData(16.0 / 9, 1, 1, 9.0 / 16)]
    [InlineData(1, 16.0 / 9, 9.0 / 16, 1)]
    public void FitFractionsAccountForLetterboxing(double sourceAspect, double canvasAspect, double width, double height)
    {
        var fitted = EditorMotionTrackingMath.FittedCanvasFraction(sourceAspect, canvasAspect);
        Assert.Equal(width, fitted.Width, 8);
        Assert.Equal(height, fitted.Height, 8);
    }

    [Theory]
    [InlineData(1, 16.0 / 9, 0, 0.25, 0, 0.25)]
    [InlineData(16.0 / 9, 1, 0.2, 0.1, 0.1, 0)]
    public void TrackingPanCancelsMotionAfterCroppingAndRefitting(double sourceAspect, double canvasAspect,
        double cropLeft, double cropTop, double cropRight, double cropBottom)
    {
        const double dx = 0.03, dy = -0.02, zoom = 1.6;
        var retainedWidth = 1 - cropLeft - cropRight;
        var retainedHeight = 1 - cropTop - cropBottom;
        var fit = EditorMotionTrackingMath.FittedCanvasFraction(sourceAspect * retainedWidth / retainedHeight, canvasAspect);
        var pan = EditorMotionTrackingMath.PanForSourceMotion(dx, dy, zoom, sourceAspect, canvasAspect,
            cropLeft, cropTop, cropRight, cropBottom);
        Assert.Equal(0, zoom * dx / retainedWidth * fit.Width + (1 - zoom) * pan.X / 2, 8);
        Assert.Equal(0, zoom * dy / retainedHeight * fit.Height + (1 - zoom) * pan.Y / 2, 8);
    }

    private static byte[] Frame(int left, int top, int brightness = 0)
    {
        var frame = Enumerable.Repeat((byte)(25 + brightness), Width * Height).ToArray();
        var random = new Random(1947);
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 18; x++)
                frame[(top + y) * Width + left + x] = (byte)(random.Next(50, 210) + brightness);
        return frame;
    }
}
