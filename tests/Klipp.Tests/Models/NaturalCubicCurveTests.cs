using Klipp.Core.Models;

namespace Klipp.Tests.Models;

public sealed class NaturalCubicCurveTests
{
    [Fact]
    public void EmptyCurveBuildsIdentityTable()
    {
        NaturalCubicCurve.BuildLookupTable([], 5).ShouldBe([0f, 0.25f, 0.5f, 0.75f, 1f]);
    }

    [Fact]
    public void NonuniformKnotsUseNaturalSplineRatherThanLinearInterpolation()
    {
        PiecewiseLinearCurvePoint[] points = [new(0, 0), new(0.25, 0.5), new(1, 1)];

        NaturalCubicCurve.Evaluate(points, 0.125).ShouldBe(0.265625, 0.000001);
        NaturalCubicCurve.Evaluate(points, 0.625).ShouldBe(0.890625, 0.000001);
        NaturalCubicCurve.Evaluate(points, 0.25).ShouldBe(0.5, 0.000001);
    }

    [Fact]
    public void ExposedSegmentsUseLocalUnclampedCubicCoefficients()
    {
        var segments = NaturalCubicCurve.GetSegmentsNormalized([new(0, 0), new(0.25, 0.5), new(1, 1)]);

        segments.Count.ShouldBe(2);
        var segment = segments[1];
        var offset = 0.625 - segment.StartX;
        var value = segment.A + offset * (segment.B + offset * (segment.C + offset * segment.D));
        value.ShouldBe(0.890625, 0.000001);
        segments[0].C.ShouldBe(0, 0.000001);
        (2 * segment.C + 6 * segment.D * (segment.EndX - segment.StartX)).ShouldBe(0, 0.000001);
    }

    [Fact]
    public void NormalizationRemovesInvalidPointsAndKeepsDistinctQuantizedPositions()
    {
        var points = NaturalCubicCurve.NormalizePoints(
        [
            new(-1, -0.5), new(0.5, 0.2), new(0.5001, 0.8),
            new(double.NaN, 0.4), new(0.8, double.PositiveInfinity), new(2, 2)
        ]);

        points.ShouldBe([new(0, 0), new(0.5001, 0.8), new(1, 1)]);
    }

    [Fact]
    public void MissingEndpointsUseConstantPaddingAndTruncatedOutputSamples()
    {
        var table = NaturalCubicCurve.BuildLookupTable([new(0.25, 0.2), new(0.75, 0.8)], 5);

        table.ShouldBe([0f, 0f, 0.5f, 0.75f, 0.75f]);
    }

    [Fact]
    public void SplineOvershootIsClampedToChannelRange()
    {
        PiecewiseLinearCurvePoint[] points = [new(0, 0), new(0.1, 1), new(0.2, 0), new(1, 1)];
        var table = NaturalCubicCurve.BuildLookupTable(points);

        table.Length.ShouldBe(256);
        table.All(value => value >= 0 && value <= 1 && float.IsFinite(value)).ShouldBeTrue();
        NaturalCubicCurve.Evaluate(points, 0.05).ShouldBeInRange(0, 1);
    }

    [Fact]
    public void SinglePointBuildsConstantClampedTable()
    {
        NaturalCubicCurve.BuildLookupTable([new(0.4, 2)], 5).ShouldBe([1f, 1f, 1f, 1f, 1f]);
    }
}
