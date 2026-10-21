using System;
using System.Collections.Generic;
using System.Linq;

namespace Klipp.Core.Models;

public readonly record struct PiecewiseLinearCurvePoint(double X, double Y);

public static class PiecewiseLinearCurve
{
    public static float[] BuildLookupTable(
        IEnumerable<PiecewiseLinearCurvePoint> points,
        int sampleCount = 256)
    {
        ArgumentNullException.ThrowIfNull(points);
        sampleCount = Math.Clamp(sampleCount, 2, 4096);
        var ordered = points
            .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
            .Select(point => new PiecewiseLinearCurvePoint(
                Math.Clamp(point.X, 0, 1),
                Math.Clamp(point.Y, 0, 1)))
            .GroupBy(point => point.X)
            .Select(group => group.Last())
            .OrderBy(point => point.X)
            .ToList();

        var table = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var value = index / (double)(sampleCount - 1);
            table[index] = (float)Evaluate(ordered, value);
        }

        return table;
    }

    public static double Evaluate(
        IReadOnlyList<PiecewiseLinearCurvePoint> orderedPoints,
        double value)
    {
        ArgumentNullException.ThrowIfNull(orderedPoints);
        value = Math.Clamp(value, 0, 1);
        if (orderedPoints.Count == 0)
            return value;
        if (value <= orderedPoints[0].X)
            return Math.Clamp(orderedPoints[0].Y, 0, 1);

        for (var index = 1; index < orderedPoints.Count; index++)
        {
            var right = orderedPoints[index];
            if (value > right.X)
                continue;

            var left = orderedPoints[index - 1];
            var span = Math.Max(0.000001, right.X - left.X);
            var progress = Math.Clamp((value - left.X) / span, 0, 1);
            return Math.Clamp(left.Y + (right.Y - left.Y) * progress, 0, 1);
        }

        return Math.Clamp(orderedPoints[^1].Y, 0, 1);
    }
}
