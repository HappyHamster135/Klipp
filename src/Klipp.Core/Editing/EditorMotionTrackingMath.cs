namespace Klipp.Core.Editing;

public readonly record struct EditorTrackingRegion(double X, double Y, double Width, double Height)
{
    public EditorTrackingRegion Clamp() => new(
        Math.Clamp(X, 0, 0.95), Math.Clamp(Y, 0, 0.95),
        Math.Clamp(Width, 0.025, 1 - Math.Clamp(X, 0, 0.95)),
        Math.Clamp(Height, 0.025, 1 - Math.Clamp(Y, 0, 0.95)));
}

public readonly record struct EditorTrackingMatch(double CenterX, double CenterY, double Confidence);

/// <summary>Tracks translation of a fixed grayscale template using normalized correlation.</summary>
public sealed class EditorMotionTemplateTracker
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _patchWidth;
    private readonly int _patchHeight;
    private readonly int _sampleStep;
    private readonly int _radius;
    private readonly double[] _template;
    private readonly byte[] _smoothedFrame;
    private readonly double _templateEnergy;
    private int _left;
    private int _top;
    private int _velocityX;
    private int _velocityY;

    public EditorTrackingMatch InitialMatch { get; }

    public EditorMotionTemplateTracker(ReadOnlySpan<byte> firstFrame, int width, int height,
        EditorTrackingRegion region, int searchRadius = 24)
    {
        if (width < 16 || height < 16 || firstFrame.Length != checked(width * height))
            throw new ArgumentException("The tracking frame has invalid dimensions.", nameof(firstFrame));
        region = region.Clamp();
        _width = width; _height = height;
        _smoothedFrame = new byte[firstFrame.Length];
        Smooth(firstFrame, _smoothedFrame, width, height);
        _patchWidth = Math.Clamp((int)Math.Round(region.Width * width), 6, width);
        _patchHeight = Math.Clamp((int)Math.Round(region.Height * height), 6, height);
        _left = Math.Clamp((int)Math.Round(region.X * width), 0, width - _patchWidth);
        _top = Math.Clamp((int)Math.Round(region.Y * height), 0, height - _patchHeight);
        _sampleStep = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(_patchWidth * _patchHeight / 625.0)));
        _radius = Math.Clamp(searchRadius, 4, 96);
        var values = new List<double>();
        for (var y = 0; y < _patchHeight; y += _sampleStep)
            for (var x = 0; x < _patchWidth; x += _sampleStep)
                values.Add(_smoothedFrame[(_top + y) * width + _left + x]);
        var mean = values.Average();
        _template = values.Select(value => value - mean).ToArray();
        _templateEnergy = _template.Sum(value => value * value);
        if (_templateEnergy / _template.Length < 20)
            throw new ArgumentException("Choose a region with more texture or detail.", nameof(region));
        InitialMatch = Position(1);
    }

    public bool TryTrack(ReadOnlySpan<byte> frame, out EditorTrackingMatch match, double minimumConfidence = 0.65)
    {
        if (frame.Length != checked(_width * _height))
            throw new ArgumentException("Tracking frame dimensions changed.", nameof(frame));
        Smooth(frame, _smoothedFrame, _width, _height);
        frame = _smoothedFrame;
        var predictedX = Math.Clamp(_left + _velocityX, 0, _width - _patchWidth);
        var predictedY = Math.Clamp(_top + _velocityY, 0, _height - _patchHeight);
        var minX = Math.Max(0, predictedX - _radius);
        var maxX = Math.Min(_width - _patchWidth, predictedX + _radius);
        var minY = Math.Max(0, predictedY - _radius);
        var maxY = Math.Min(_height - _patchHeight, predictedY + _radius);
        var bestX = _left; var bestY = _top;
        var best = Correlation(frame, bestX, bestY);
        var bestDistance = (bestX - predictedX) * (bestX - predictedX) + (bestY - predictedY) * (bestY - predictedY);
        var step = _radius > 12 ? 3 : 2;
        for (var y = minY; y <= maxY; y += step)
            for (var x = minX; x <= maxX; x += step)
                Consider(frame, x, y, predictedX, predictedY, ref best, ref bestDistance, ref bestX, ref bestY);
        var coarseX = bestX; var coarseY = bestY;
        for (var y = Math.Max(minY, coarseY - step); y <= Math.Min(maxY, coarseY + step); y++)
            for (var x = Math.Max(minX, coarseX - step); x <= Math.Min(maxX, coarseX + step); x++)
                Consider(frame, x, y, predictedX, predictedY, ref best, ref bestDistance, ref bestX, ref bestY);
        if (best < minimumConfidence)
        {
            match = Position(Math.Max(0, best));
            return false;
        }
        _velocityX = Math.Clamp(bestX - _left, -_radius / 2, _radius / 2);
        _velocityY = Math.Clamp(bestY - _top, -_radius / 2, _radius / 2);
        _left = bestX; _top = bestY;
        match = Position(Math.Clamp(best, 0, 1));
        return true;
    }

    private void Consider(ReadOnlySpan<byte> frame, int x, int y, int predictedX, int predictedY,
        ref double best, ref int bestDistance, ref int bestX, ref int bestY)
    {
        var score = Correlation(frame, x, y);
        var distance = (x - predictedX) * (x - predictedX) + (y - predictedY) * (y - predictedY);
        if (score > best + 0.000001 || Math.Abs(score - best) <= 0.000001 && distance < bestDistance)
        {
            best = score; bestDistance = distance; bestX = x; bestY = y;
        }
    }

    private double Correlation(ReadOnlySpan<byte> frame, int left, int top)
    {
        var sum = 0.0; var squared = 0.0; var product = 0.0; var index = 0;
        for (var y = 0; y < _patchHeight; y += _sampleStep)
            for (var x = 0; x < _patchWidth; x += _sampleStep)
            {
                var value = frame[(top + y) * _width + left + x];
                sum += value; squared += value * value;
                product += value * _template[index++];
            }
        var energy = squared - sum * sum / _template.Length;
        return energy < 1 ? -1 : product / Math.Sqrt(_templateEnergy * energy);
    }

    private EditorTrackingMatch Position(double confidence) =>
        new((_left + _patchWidth / 2.0) / _width, (_top + _patchHeight / 2.0) / _height, confidence);

    private static void Smooth(ReadOnlySpan<byte> source, Span<byte> target, int width, int height)
    {
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var total = 0;
                for (var row = Math.Max(0, y - 1); row <= Math.Min(height - 1, y + 1); row++)
                    for (var column = Math.Max(0, x - 1); column <= Math.Min(width - 1, x + 1); column++)
                        total += source[row * width + column];
                var samples = (Math.Min(height - 1, y + 1) - Math.Max(0, y - 1) + 1) *
                    (Math.Min(width - 1, x + 1) - Math.Max(0, x - 1) + 1);
                target[y * width + x] = (byte)(total / samples);
            }
    }
}

public static class EditorMotionTrackingMath
{
    /// <summary>Converts source motion into the editor's zoom-relative pan coordinates.</summary>
    public static double PanForDisplacement(double sourceDisplacement, double zoom, double fittedCanvasFraction = 1) =>
        zoom <= 1.000001 ? 0 : 2 * zoom * sourceDisplacement * fittedCanvasFraction / (zoom - 1);

    public static (double Width, double Height) FittedCanvasFraction(double sourceAspect, double canvasAspect) =>
        sourceAspect >= canvasAspect ? (1, canvasAspect / sourceAspect) : (sourceAspect / canvasAspect, 1);

    public static (double X, double Y) PanForSourceMotion(double displacementX, double displacementY,
        double zoom, double sourceAspect, double canvasAspect,
        double cropLeft = 0, double cropTop = 0, double cropRight = 0, double cropBottom = 0)
    {
        var retainedWidth = Math.Clamp(1 - cropLeft - cropRight, 0.1, 1);
        var retainedHeight = Math.Clamp(1 - cropTop - cropBottom, 0.1, 1);
        var fit = FittedCanvasFraction(sourceAspect * retainedWidth / retainedHeight, canvasAspect);
        return (PanForDisplacement(displacementX / retainedWidth, zoom, fit.Width),
            PanForDisplacement(displacementY / retainedHeight, zoom, fit.Height));
    }
}
