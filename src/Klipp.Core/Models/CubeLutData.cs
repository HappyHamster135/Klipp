using System.Globalization;
using System.Numerics;

namespace Klipp.Core.Models;

/// <summary>Validated RGB .cube samples with the red coordinate varying fastest.</summary>
public sealed class CubeLutData
{
    private readonly Vector3[] _colors;
    public int Size { get; }
    public Vector3 DomainMinimum { get; }
    public Vector3 DomainMaximum { get; }

    private CubeLutData(int size, Vector3 minimum, Vector3 maximum, Vector3[] colors)
    {
        Size = size;
        DomainMinimum = minimum;
        DomainMaximum = maximum;
        _colors = colors;
    }

    public static CubeLutData Parse(IEnumerable<string> lines)
    {
        var size = 0;
        var minimum = Vector3.Zero;
        var maximum = Vector3.One;
        var colors = new List<Vector3>();
        foreach (var raw in lines)
        {
            var line = raw.Split('#', 2)[0].Trim();
            if (line.Length == 0 || line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
                continue;
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (tokens[0].ToUpperInvariant())
            {
                case "LUT_3D_SIZE":
                    if (tokens.Length != 2 || size != 0 || !int.TryParse(tokens[1], out size) || size is < 2 or > 128)
                        throw new FormatException("The LUT must declare one 3D size between 2 and 128.");
                    colors.Capacity = checked(size * size * size);
                    continue;
                case "DOMAIN_MIN": minimum = ReadColor(tokens.Skip(1).ToArray()); continue;
                case "DOMAIN_MAX": maximum = ReadColor(tokens.Skip(1).ToArray()); continue;
                case "LUT_1D_SIZE": throw new FormatException("Choose a 3D .cube LUT.");
            }
            if (size == 0 || colors.Count >= size * size * size)
                throw new FormatException("The LUT contains unexpected color rows.");
            colors.Add(ReadColor(tokens));
        }
        if (size == 0 || colors.Count != size * size * size ||
            maximum.X <= minimum.X || maximum.Y <= minimum.Y || maximum.Z <= minimum.Z)
            throw new FormatException("The LUT color count or input domain is invalid.");
        return new CubeLutData(size, minimum, maximum, colors.ToArray());
    }

    private static Vector3 ReadColor(string[] tokens)
    {
        if (tokens.Length != 3 || !float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ||
            !float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ||
            !float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ||
            !float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
            throw new FormatException("The LUT contains an invalid color.");
        return new Vector3(r, g, b);
    }

    public Vector3 Sample(Vector3 input)
    {
        var point = Vector3.Clamp((input - DomainMinimum) / (DomainMaximum - DomainMinimum), Vector3.Zero, Vector3.One) * (Size - 1);
        var r = Math.Min(Size - 2, (int)point.X);
        var g = Math.Min(Size - 2, (int)point.Y);
        var b = Math.Min(Size - 2, (int)point.Z);
        var f = point - new Vector3(r, g, b);
        Vector3 C(int dr, int dg, int db) => _colors[((b + db) * Size + g + dg) * Size + r + dr];
        var origin = C(0, 0, 0);
        var end = C(1, 1, 1);
        if (f.X >= f.Y)
        {
            if (f.Y >= f.Z) return origin + f.X * (C(1, 0, 0) - origin) + f.Y * (C(1, 1, 0) - C(1, 0, 0)) + f.Z * (end - C(1, 1, 0));
            if (f.X >= f.Z) return origin + f.X * (C(1, 0, 0) - origin) + f.Z * (C(1, 0, 1) - C(1, 0, 0)) + f.Y * (end - C(1, 0, 1));
            return origin + f.Z * (C(0, 0, 1) - origin) + f.X * (C(1, 0, 1) - C(0, 0, 1)) + f.Y * (end - C(1, 0, 1));
        }
        if (f.X >= f.Z) return origin + f.Y * (C(0, 1, 0) - origin) + f.X * (C(1, 1, 0) - C(0, 1, 0)) + f.Z * (end - C(1, 1, 0));
        if (f.Y >= f.Z) return origin + f.Y * (C(0, 1, 0) - origin) + f.Z * (C(0, 1, 1) - C(0, 1, 0)) + f.X * (end - C(0, 1, 1));
        return origin + f.Z * (C(0, 0, 1) - origin) + f.Y * (C(0, 1, 1) - C(0, 0, 1)) + f.X * (end - C(0, 1, 1));
    }

    /// <summary>Dense float RGBA transfer table, with blue varying fastest for Direct2D.</summary>
    public byte[] BuildBlueFastTransferTable(int resolution = 65)
    {
        resolution = Math.Clamp(resolution, 2, 129);
        var values = new float[checked(resolution * resolution * resolution * 4)];
        var index = 0;
        for (var r = 0; r < resolution; r++)
            for (var g = 0; g < resolution; g++)
                for (var b = 0; b < resolution; b++)
                {
                    var color = Sample(new Vector3(r, g, b) / (resolution - 1));
                    values[index++] = color.X; values[index++] = color.Y; values[index++] = color.Z; values[index++] = 1;
                }
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
