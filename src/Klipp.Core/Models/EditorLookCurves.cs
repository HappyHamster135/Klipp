using System;
using System.Collections.Generic;

namespace Klipp.Core.Models;

/// <summary>Channel curves shared by the native preview and rendered exports.</summary>
public sealed record EditorLookCurveSet(
    IReadOnlyList<PiecewiseLinearCurvePoint> Red,
    IReadOnlyList<PiecewiseLinearCurvePoint> Green,
    IReadOnlyList<PiecewiseLinearCurvePoint> Blue);

public static class EditorLookCurves
{
    // Keep these grades restrained at full strength. Endpoints and gradual
    // shoulders preserve detail instead of clipping bright or dark footage.
    private static readonly IReadOnlyDictionary<string, EditorLookCurveSet> Looks =
        new Dictionary<string, EditorLookCurveSet>(StringComparer.Ordinal)
        {
            ["natural"] = new(
                Curve((0, 0), (0.2, 0.188), (0.5, 0.508), (0.8, 0.818), (1, 1)),
                Curve((0, 0), (0.2, 0.19), (0.5, 0.505), (0.8, 0.815), (1, 1)),
                Curve((0, 0), (0.2, 0.19), (0.5, 0.501), (0.8, 0.81), (1, 1))),
            ["matte"] = new(
                Curve((0, 0.045), (0.2, 0.225), (0.5, 0.515), (0.8, 0.785), (1, 0.97)),
                Curve((0, 0.043), (0.2, 0.223), (0.5, 0.511), (0.8, 0.78), (1, 0.968)),
                Curve((0, 0.047), (0.2, 0.228), (0.5, 0.511), (0.8, 0.775), (1, 0.96))),
            ["goldenhour"] = new(
                Curve((0, 0), (0.2, 0.202), (0.5, 0.525), (0.8, 0.839), (1, 0.995)),
                Curve((0, 0), (0.2, 0.2), (0.5, 0.508), (0.8, 0.812), (1, 0.99)),
                Curve((0, 0), (0.2, 0.198), (0.5, 0.483), (0.8, 0.77), (1, 0.965))),
            ["steel"] = new(
                Curve((0, 0.004), (0.2, 0.176), (0.5, 0.482), (0.8, 0.811), (1, 0.99)),
                Curve((0, 0.006), (0.2, 0.188), (0.5, 0.498), (0.8, 0.817), (1, 0.995)),
                Curve((0, 0.012), (0.2, 0.214), (0.5, 0.521), (0.8, 0.825), (1, 1)))
        };

    public static EditorLookCurveSet? Find(string? id) =>
        id is not null && Looks.TryGetValue(id, out var curves) ? curves : null;

    private static IReadOnlyList<PiecewiseLinearCurvePoint> Curve(params (double X, double Y)[] points) =>
        Array.AsReadOnly(Array.ConvertAll(points, point => new PiecewiseLinearCurvePoint(point.X, point.Y)));
}
