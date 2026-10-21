using Klipp.Core.Models;

namespace Klipp.Tests.Models;

public sealed class PiecewiseLinearCurveTests
{
    [Fact]
    public void EmptyCurveBuildsIdentityTable()
    {
        var table = PiecewiseLinearCurve.BuildLookupTable([], sampleCount: 5);

        table.ShouldBe([0f, 0.25f, 0.5f, 0.75f, 1f]);
    }

    [Fact]
    public void LookupTableInterpolatesAndClampsPoints()
    {
        var table = PiecewiseLinearCurve.BuildLookupTable(
            [
                new PiecewiseLinearCurvePoint(-1, -1),
                new PiecewiseLinearCurvePoint(0.5, 0.75),
                new PiecewiseLinearCurvePoint(2, 2)
            ],
            sampleCount: 5);

        table.ShouldBe([0f, 0.375f, 0.75f, 0.875f, 1f], tolerance: 0.0001f);
    }

    [Fact]
    public void DuplicatePositionsUseLastFinitePoint()
    {
        var table = PiecewiseLinearCurve.BuildLookupTable(
            [
                new PiecewiseLinearCurvePoint(0, 0),
                new PiecewiseLinearCurvePoint(0.5, 0.2),
                new PiecewiseLinearCurvePoint(0.5, 0.8),
                new PiecewiseLinearCurvePoint(double.NaN, 0.4),
                new PiecewiseLinearCurvePoint(1, 1)
            ],
            sampleCount: 3);

        table.ShouldBe([0f, 0.8f, 1f], tolerance: 0.0001f);
    }
}
