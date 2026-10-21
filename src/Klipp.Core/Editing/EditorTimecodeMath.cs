using System.Globalization;

namespace Klipp.Core.Editing;

/// <summary>Non-drop timecode entry for the project's integer editing frame rate.</summary>
public static class EditorTimecodeMath
{
    public static string Format(double seconds, int frameRate)
    {
        frameRate = Math.Max(1, frameRate);
        var totalFrames = (long)Math.Round(Math.Max(0, seconds) * frameRate, MidpointRounding.AwayFromZero);
        var totalSeconds = totalFrames / frameRate;
        return $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}:{totalFrames % frameRate:00}";
    }

    public static bool TryParsePosition(string? input, double currentSeconds, double durationSeconds,
        int frameRate, out double positionSeconds, out string error)
    {
        positionSeconds = 0;
        error = "Use HH:MM:SS:FF, MM:SS, or seconds. Add + or - for a relative move.";
        if (frameRate <= 0 || !double.IsFinite(currentSeconds) ||
            !double.IsFinite(durationSeconds) || durationSeconds < 0)
            return false;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        var relative = text[0] is '+' or '-';
        var sign = text[0] == '-' ? -1 : 1;
        if (relative) text = text[1..].Trim();
        var parts = text.Split(':');
        if (parts.Length is < 1 or > 4) return false;

        var amount = 0d;
        if (parts.Length == 1)
        {
            if (!TrySeconds(parts[0], out amount)) return false;
        }
        else
        {
            var values = new long[parts.Length];
            // The last component is fractional seconds unless frames are present.
            var integerCount = parts.Length == 4 ? 4 : parts.Length - 1;
            for (var i = 0; i < integerCount; i++)
                if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
                    return false;
            var secondsIndex = parts.Length == 2 ? 1 : 2;
            double seconds;
            if (parts.Length == 4)
            {
                seconds = values[secondsIndex];
                if (values[3] >= frameRate)
                {
                    error = $"Frames must be between 00 and {frameRate - 1:00} at {frameRate} fps.";
                    return false;
                }
            }
            else if (!TrySeconds(parts[secondsIndex], out seconds)) return false;
            if (seconds >= 60 || parts.Length >= 3 && values[1] >= 60)
            {
                error = "Minutes and seconds in a timecode must be between 00 and 59.";
                return false;
            }
            amount = parts.Length == 2 ? values[0] * 60d + seconds
                : values[0] * 3600d + values[1] * 60d + seconds;
            if (parts.Length == 4) amount += values[3] / (double)frameRate;
        }

        var target = relative ? currentSeconds + sign * amount : amount;
        if (!double.IsFinite(target) || target < -0.0000001 || target > durationSeconds + 0.0000001)
        {
            var lastPosition = Math.Floor((durationSeconds + .0000001) * frameRate) / frameRate;
            error = $"Choose a position from {Format(0, frameRate)} to {Format(lastPosition, frameRate)}.";
            return false;
        }
        var lastFrame = Math.Floor((durationSeconds + 0.0000001) * frameRate);
        var requestedFrame = Math.Round(Math.Max(0, target) * frameRate, MidpointRounding.AwayFromZero);
        positionSeconds = Math.Min(lastFrame, requestedFrame) / frameRate;
        error = string.Empty;
        return true;
    }

    private static bool TrySeconds(string text, out double seconds) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds) &&
        double.IsFinite(seconds) && seconds >= 0;
}
