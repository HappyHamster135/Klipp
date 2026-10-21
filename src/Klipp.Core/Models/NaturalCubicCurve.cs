using System;
using System.Collections.Generic;
using System.Linq;

namespace Klipp.Core.Models;

/// <summary>Y = A + B*u + C*u² + D*u³, where u = X - StartX, before output clamping.</summary>
public readonly record struct NaturalCubicCurveSegment(
    double StartX, double EndX, double A, double B, double C, double D);

/// <summary>Natural cubic tone curves using the same sampled 8-bit mapping as FFmpeg curves.</summary>
public static class NaturalCubicCurve
{
    public static IReadOnlyList<NaturalCubicCurveSegment> GetSegmentsNormalized(
        IEnumerable<PiecewiseLinearCurvePoint> points)
    {
        var normalized = NormalizePoints(points);
        var second = SolveSecondDerivatives(normalized);
        var segments = new List<NaturalCubicCurveSegment>();
        for (var index = 0; index + 1 < normalized.Count; index++)
        {
            var left = normalized[index];
            var right = normalized[index + 1];
            var span = right.X - left.X;
            segments.Add(new(left.X, right.X, left.Y,
                (right.Y - left.Y) / span - span * (2 * second[index] + second[index + 1]) / 6,
                second[index] / 2, (second[index + 1] - second[index]) / (6 * span)));
        }
        return segments;
    }

    public static IReadOnlyList<PiecewiseLinearCurvePoint> NormalizePoints(
        IEnumerable<PiecewiseLinearCurvePoint> points,
        int sampleCount = 256)
    {
        ArgumentNullException.ThrowIfNull(points);
        var scale = Math.Clamp(sampleCount, 2, 4096) - 1;
        return points
            .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
            .Select(point => new PiecewiseLinearCurvePoint(Math.Clamp(point.X, 0, 1), Math.Clamp(point.Y, 0, 1)))
            .OrderBy(point => point.X)
            // FFmpeg rejects two knots whose X coordinates truncate to the same LUT entry.
            .GroupBy(point => (int)(point.X * scale))
            .Select(group => group.Last())
            .ToList();
    }

    public static float[] BuildLookupTable(
        IEnumerable<PiecewiseLinearCurvePoint> points,
        int sampleCount = 256)
    {
        sampleCount = Math.Clamp(sampleCount, 2, 4096);
        var normalized = NormalizePoints(points, sampleCount);
        var table = new float[sampleCount];
        var scale = sampleCount - 1;
        if (normalized.Count == 0)
        {
            for (var index = 0; index < sampleCount; index++)
                table[index] = index / (float)scale;
            return table;
        }

        static float Quantize(double value, int maximum) => (int)Math.Clamp(value * maximum, 0, maximum) / (float)maximum;
        Array.Fill(table, Quantize(normalized[0].Y, scale));
        var secondDerivatives = SolveSecondDerivatives(normalized);
        for (var interval = 0; interval + 1 < normalized.Count; interval++)
        {
            var start = (int)(normalized[interval].X * scale);
            var end = (int)(normalized[interval + 1].X * scale);
            for (var index = start; index <= end; index++)
            {
                // FFmpeg anchors each segment at the truncated sample index,
                // then truncates its output to an integer channel value.
                var offset = (index - start) / (double)scale;
                table[index] = Quantize(EvaluateInterval(normalized, secondDerivatives, interval, offset), scale);
            }
        }
        var finalIndex = (int)(normalized[^1].X * scale);
        Array.Fill(table, Quantize(normalized[^1].Y, scale), finalIndex, sampleCount - finalIndex);
        return table;
    }

    public static double Evaluate(IEnumerable<PiecewiseLinearCurvePoint> points, double value)
    {
        var normalized = NormalizePoints(points);
        value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        if (normalized.Count == 0)
            return value;
        if (value <= normalized[0].X)
            return normalized[0].Y;
        if (value >= normalized[^1].X)
            return normalized[^1].Y;
        var interval = 0;
        while (interval + 1 < normalized.Count && value > normalized[interval + 1].X)
            interval++;
        return Math.Clamp(EvaluateInterval(normalized, SolveSecondDerivatives(normalized), interval,
            value - normalized[interval].X), 0, 1);
    }

    private static double[] SolveSecondDerivatives(IReadOnlyList<PiecewiseLinearCurvePoint> points)
    {
        var count = points.Count;
        var second = new double[count];
        var upper = new double[count];
        for (var index = 1; index + 1 < count; index++)
        {
            var previousSpan = points[index].X - points[index - 1].X;
            var nextSpan = points[index + 1].X - points[index].X;
            var previousSlope = (points[index].Y - points[index - 1].Y) / previousSpan;
            var nextSlope = (points[index + 1].Y - points[index].Y) / nextSpan;
            var denominator = 2 * (previousSpan + nextSpan) - previousSpan * upper[index - 1];
            upper[index] = nextSpan / denominator;
            second[index] = (6 * (nextSlope - previousSlope) - previousSpan * second[index - 1]) / denominator;
        }
        for (var index = count - 2; index >= 0; index--)
            second[index] -= upper[index] * second[index + 1];
        return second;
    }

    private static double EvaluateInterval(
        IReadOnlyList<PiecewiseLinearCurvePoint> points,
        IReadOnlyList<double> second,
        int interval,
        double offset)
    {
        var left = points[interval];
        var right = points[interval + 1];
        var span = right.X - left.X;
        var slope = (right.Y - left.Y) / span - span * (2 * second[interval] + second[interval + 1]) / 6;
        return left.Y + offset * (slope + offset * (second[interval] / 2 + offset *
            (second[interval + 1] - second[interval]) / (6 * span)));
    }
}
