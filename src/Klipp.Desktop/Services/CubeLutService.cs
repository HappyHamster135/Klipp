using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Klipp.Desktop.Services;

public sealed record CubeLutInfo(string Path, string Name, int Size, int ColorCount);

public sealed class CubeLutService
{
    private const long MaximumFileSize = 64L * 1024 * 1024;

    public async Task<CubeLutInfo> ValidateAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("The LUT file no longer exists.", filePath);
        if (!string.Equals(Path.GetExtension(filePath), ".cube", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a .cube 3D LUT file.");

        var file = new FileInfo(filePath);
        if (file.Length <= 0 || file.Length > MaximumFileSize)
            throw new InvalidOperationException("The LUT file is empty or too large.");

        var size = 0;
        var colorCount = 0;
        string? title = null;
        double[] domainMinimum = [0, 0, 0];
        double[] domainMaximum = [1, 1, 1];
        var hasDomainMinimum = false;
        var hasDomainMaximum = false;
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } rawLine)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commentIndex = FindCommentStart(rawLine);
            var line = (commentIndex >= 0 ? rawLine[..commentIndex] : rawLine).Trim();
            if (line.Length == 0)
                continue;

            var values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var directive = values[0];
            if (string.Equals(directive, "TITLE", StringComparison.OrdinalIgnoreCase))
            {
                title = line["TITLE".Length..].Trim().Trim('"');
                continue;
            }

            if (string.Equals(directive, "LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Klipp supports 3D .cube LUTs; this file contains a 1D LUT.");

            if (string.Equals(directive, "LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                if (size != 0 || colorCount != 0)
                    throw new InvalidOperationException("The LUT must declare one 3D size before its color rows.");
                var sizeText = line["LUT_3D_SIZE".Length..].Trim();
                if (!int.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out size) ||
                    size is < 2 or > 128)
                {
                    throw new InvalidOperationException("The LUT has an unsupported 3D size.");
                }
                continue;
            }

            if (string.Equals(directive, "DOMAIN_MIN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directive, "DOMAIN_MAX", StringComparison.OrdinalIgnoreCase))
            {
                var isMinimum = string.Equals(directive, "DOMAIN_MIN", StringComparison.OrdinalIgnoreCase);
                if (colorCount != 0 || (isMinimum ? hasDomainMinimum : hasDomainMaximum) ||
                    values.Length != 4 || !TryReadColor(values.AsSpan(1), out var red, out var green, out var blue))
                {
                    throw new InvalidOperationException("The LUT contains an invalid or repeated input domain.");
                }
                if (isMinimum)
                {
                    domainMinimum = [red, green, blue];
                    hasDomainMinimum = true;
                }
                else
                {
                    domainMaximum = [red, green, blue];
                    hasDomainMaximum = true;
                }
                continue;
            }

            if (size == 0 || values.Length != 3 || !TryReadColor(values, out _, out _, out _))
            {
                throw new InvalidOperationException("The LUT contains an invalid color row.");
            }

            colorCount++;
            if (colorCount > size * size * size)
                throw new InvalidOperationException("The LUT contains more colors than its declared 3D size.");
        }

        if (size == 0)
            throw new InvalidOperationException("The file does not declare LUT_3D_SIZE.");

        for (var channel = 0; channel < 3; channel++)
        {
            if (domainMinimum[channel] >= domainMaximum[channel])
                throw new InvalidOperationException("Every LUT input domain maximum must be greater than its minimum.");
        }

        var expectedColorCount = checked(size * size * size);
        if (colorCount != expectedColorCount)
        {
            throw new InvalidOperationException(
                $"The LUT declares {size}x{size}x{size}, but contains {colorCount} of {expectedColorCount} colors.");
        }

        var name = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(filePath)
            : title;
        return new CubeLutInfo(Path.GetFullPath(filePath), name, size, colorCount);
    }

    private static bool TryReadColor(ReadOnlySpan<string> values, out double red, out double green, out double blue)
    {
        red = green = blue = 0;
        return values.Length == 3 &&
               double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out red) &&
               double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out green) &&
               double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out blue) &&
               double.IsFinite(red) && double.IsFinite(green) && double.IsFinite(blue);
    }

    private static int FindCommentStart(string line)
    {
        var inQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
                inQuotes = !inQuotes;
            else if (line[index] == '#' && !inQuotes)
                return index;
        }
        return -1;
    }
}
