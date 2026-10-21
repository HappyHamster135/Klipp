namespace Klipp.Core.Editing;

public static class EditorKeyframeTrimMath
{
    public static IReadOnlyList<T> SampleTruncatedCurve<T>(IReadOnlyList<T> source, double boundary, bool keepAfter,
        Func<T, double> timeOf, Func<T, string> easingOf, Func<double, T> sampleAt)
    {
        var ordered = source.OrderBy(timeOf).ToList();
        var rightIndex = ordered.FindIndex(key => timeOf(key) > boundary + .0001);
        if (rightIndex <= 0)
            return source;
        var left = timeOf(ordered[rightIndex - 1]);
        var right = timeOf(ordered[rightIndex]);
        if (boundary <= left + .0001 || easingOf(ordered[rightIndex]) is not ("ease-in" or "ease-out" or "ease-in-out"))
            return source;
        var start = keepAfter ? boundary : left;
        var end = keepAfter ? right : boundary;
        var count = Math.Clamp((int)Math.Ceiling((end - start) * 120), 1, 256);
        for (var index = keepAfter ? 0 : 1; index <= count; index++)
        {
            var time = start + (end - start) * index / count;
            ordered.RemoveAll(key => Math.Abs(timeOf(key) - time) < .000001);
            ordered.Add(sampleAt(time));
        }
        return ordered.OrderBy(timeOf).ToList();
    }
}
