using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Klipp.Core.Updates;

/// <summary>Small, plain-text highlights from the exact release's authored notes.</summary>
public static class AppUpdateReleaseNotes
{
    public const int MaximumHighlights = 3;
    public const int MaximumHighlightLength = 100;
    private const int MaximumInputLength = 32_768;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<string> Summarize(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<string>();
        var input = markdown[..Math.Min(markdown.Length, MaximumInputLength)];
        var bullets = new List<string>(MaximumHighlights);
        var paragraphs = new List<string>(MaximumHighlights);
        var inCode = false;
        foreach (var line in input.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode || trimmed.Length == 0 || trimmed[0] is '#' or '>' || trimmed is "---" or "***" ||
                trimmed.StartsWith("![", StringComparison.Ordinal)) continue;

            // Prefer top-level change bullets. Nested technical details should
            // not replace the release's primary user-facing changes.
            var indent = line.Length - line.TrimStart().Length;
            if (indent >= 2 || line.StartsWith('\t')) continue;
            var bullet = Regex.Match(trimmed, @"^(?:[-*+]|\d{1,3}[.)])\s+(.+)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeout);
            var text = PlainText(bullet.Success ? bullet.Groups[1].Value : trimmed);
            if (text.Length == 0) continue;
            var target = bullet.Success ? bullets : paragraphs;
            if (target.Count < MaximumHighlights && !target.Contains(text, StringComparer.OrdinalIgnoreCase)) target.Add(text);
            if (bullets.Count == MaximumHighlights) break;
        }
        return (bullets.Count > 0 ? bullets : paragraphs).ToArray();
    }

    private static string PlainText(string text)
    {
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeout);
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeout);
        text = Regex.Replace(text, @"<[^>]*>", "", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeout);
        text = WebUtility.HtmlDecode(text).Replace("**", "").Replace("__", "").Replace("~~", "").Replace("`", "");
        if (text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("[ ] ", StringComparison.Ordinal)) text = text[4..];
        var clean = new StringBuilder(text.Length);
        var spacePending = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character)) { spacePending = clean.Length > 0; continue; }
            if (char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format) continue;
            if (spacePending) { clean.Append(' '); spacePending = false; }
            clean.Append(character);
        }
        var result = clean.ToString().Trim();
        if (result.Length <= MaximumHighlightLength) return result;
        var cut = result.LastIndexOf(' ', MaximumHighlightLength - 1);
        if (cut < MaximumHighlightLength / 2) cut = MaximumHighlightLength - 1;
        // Keep emoji and combining characters intact when a long word needs a
        // hard limit. Leave room for a single typographic ellipsis.
        cut = StringInfo.ParseCombiningCharacters(result).Last(index => index <= cut);
        return result[..cut].TrimEnd() + "…";
    }
}
