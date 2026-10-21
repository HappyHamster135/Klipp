using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorKeyframeTrimMathTests
{
    private sealed record Point(double Time, double Value, string Easing);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrimmingAnEasedSegmentPreservesTheOriginalCurveOnTheRetainedSide(bool keepAfter)
    {
        var source = new[] { new Point(0, 0, "linear"), new Point(2, 1, "ease-in"), new Point(3, .5, "linear") };
        var sampled = EditorKeyframeTrimMath.SampleTruncatedCurve(source, 1, keepAfter,
            point => point.Time, point => point.Easing, time => new Point(time, time * time / 4, "linear"));
        var retained = sampled.Where(point => keepAfter ? point.Time >= 1 && point.Time <= 2 : point.Time <= 1).ToList();
        Assert.Equal(keepAfter ? 1 : 0, retained[0].Time);
        Assert.Equal(keepAfter ? 2 : 1, retained[^1].Time);
        for (var index = 1; index < retained.Count; index++)
        {
            var left = retained[index - 1];
            var right = retained[index];
            var midpoint = (left.Time + right.Time) / 2;
            Assert.InRange(Math.Abs((left.Value + right.Value) / 2 - midpoint * midpoint / 4), 0, .00001);
        }
        Assert.Contains(sampled, point => point.Time == 3 && point.Value == .5 && point.Easing == "linear");
    }

    [Theory]
    [InlineData("linear")]
    [InlineData("hold")]
    public void LinearAndHoldSegmentsDoNotGenerateExtraKeys(string easing)
    {
        var source = new[] { new Point(0, 0, "linear"), new Point(2, 1, easing) };
        var sampled = EditorKeyframeTrimMath.SampleTruncatedCurve(source, 1, true,
            point => point.Time, point => point.Easing, time => throw new InvalidOperationException("No sampling needed."));
        Assert.Equal(source, sampled);
    }

    [Fact]
    public void ExtendingTheLeadingEdgeDoesNotAlterExistingEasing()
    {
        var source = new[] { new Point(0, 0, "linear"), new Point(2, 1, "ease-in") };
        Assert.Equal(source, EditorKeyframeTrimMath.SampleTruncatedCurve(source, -.25, true,
            point => point.Time, point => point.Easing, time => throw new InvalidOperationException()));
    }
}
