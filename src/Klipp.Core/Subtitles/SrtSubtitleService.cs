using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Klipp.Core.Subtitles;

public static class SrtSubtitleService
{
    private static readonly string[] TimestampFormats =
    [
        @"h\:mm\:ss\.fff",
        @"hh\:mm\:ss\.fff"
    ];

    public static IReadOnlyList<SubtitleCue> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<SubtitleCue>();

        var lines = content
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var cues = new List<SubtitleCue>();
        var index = 0;

        while (index < lines.Length)
        {
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
                index++;
            if (index >= lines.Length)
                break;

            var timingLine = lines[index].Trim();
            if (!timingLine.Contains("-->", StringComparison.Ordinal) &&
                int.TryParse(timingLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                index++;
                if (index >= lines.Length)
                    break;
                timingLine = lines[index].Trim();
            }

            index++;
            if (!TryParseTiming(timingLine, out var start, out var end))
            {
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
                    index++;
                continue;
            }

            var textLines = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
                textLines.Add(lines[index++].TrimEnd());

            var text = string.Join(Environment.NewLine, textLines).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                cues.Add(new SubtitleCue(start, end, text));
        }

        return cues;
    }

    public static string Serialize(IEnumerable<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var builder = new StringBuilder();
        var index = 1;

        foreach (var cue in cues
                     .Where(cue => cue.End > cue.Start && !string.IsNullOrWhiteSpace(cue.Text))
                     .OrderBy(cue => cue.Start))
        {
            if (builder.Length > 0)
                builder.AppendLine();

            builder.AppendLine(index.ToString(CultureInfo.InvariantCulture));
            builder.Append(FormatTimestamp(cue.Start));
            builder.Append(" --> ");
            builder.AppendLine(FormatTimestamp(cue.End));
            builder.AppendLine(cue.Text.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
            index++;
        }

        return builder.ToString();
    }

    private static bool TryParseTiming(string value, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;
        var parts = value.Split("-->", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        var endToken = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return TryParseTimestamp(parts[0], out start) &&
               TryParseTimestamp(endToken, out end) &&
               end > start;
    }

    private static bool TryParseTimestamp(string value, out TimeSpan timestamp)
    {
        return TimeSpan.TryParseExact(
            value.Trim().Replace(',', '.'),
            TimestampFormats,
            CultureInfo.InvariantCulture,
            out timestamp);
    }

    private static string FormatTimestamp(TimeSpan value)
    {
        value = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        var totalHours = (long)Math.Floor(value.TotalHours);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}");
    }
}
