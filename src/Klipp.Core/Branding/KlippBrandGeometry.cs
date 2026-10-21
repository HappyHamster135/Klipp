namespace Klipp.Core.Branding;

/// <summary>Platform-independent source geometry for Klipp's split-K mark.</summary>
public static class KlippBrandGeometry
{
    public const double Size = 24;
    public static readonly KlippBrandPoint[][] Polygons =
    [
        [new(4, 4), new(8, 4), new(8, 20), new(4, 20)],
        [new(11, 4), new(20, 4), new(13, 11), new(8, 11)],
        [new(8, 13), new(13, 13), new(20, 20), new(11, 20)]
    ];

    public static bool Contains(double x, double y)
    {
        foreach (var polygon in Polygons)
        {
            var inside = false;
            for (int index = 0, previous = polygon.Length - 1; index < polygon.Length; previous = index++)
            {
                var a = polygon[index];
                var b = polygon[previous];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            if (inside) return true;
        }
        return false;
    }
}

public readonly record struct KlippBrandPoint(double X, double Y);
