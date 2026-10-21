using System;
using System.Collections.Generic;
using System.Linq;

namespace Klipp.Core.Models;

[Flags]
public enum ClipRetentionProtectionReason
{
    None = 0,
    Favorite = 1,
    ProjectReference = 2,
    WithinRetentionWindow = 4,
    MinimumLibrarySize = 8
}

public sealed record ClipRetentionItem(
    string FilePath,
    DateTime LastWriteUtc,
    long SizeBytes,
    bool IsFavorite,
    bool IsProjectReferenced);

public sealed record ClipRetentionDecision(
    ClipRetentionItem Item,
    ClipRetentionProtectionReason ProtectionReason)
{
    public bool IsCandidate => ProtectionReason == ClipRetentionProtectionReason.None;
}

public sealed class ClipRetentionPlan
{
    public required int RetentionDays { get; init; }
    public required DateTime CutoffUtc { get; init; }
    public required IReadOnlyList<ClipRetentionDecision> Decisions { get; init; }

    public IReadOnlyList<ClipRetentionItem> Candidates => Decisions
        .Where(decision => decision.IsCandidate)
        .Select(decision => decision.Item)
        .ToList();

    public long CandidateBytes => Candidates.Sum(item => Math.Max(0, item.SizeBytes));

    public int CountProtectedBy(ClipRetentionProtectionReason reason) => Decisions.Count(decision =>
        decision.ProtectionReason.HasFlag(reason));
}

public static class OriginalClipRetentionPolicy
{
    public const int DefaultRetentionDays = 90;
    public const int MinimumRetentionDays = 7;
    public const int MaximumRetentionDays = 3650;
    public const int DefaultMinimumLibrarySize = 20;

    public static int NormalizeRetentionDays(int days) =>
        Math.Clamp(days, MinimumRetentionDays, MaximumRetentionDays);

    public static ClipRetentionPlan CreatePlan(
        IEnumerable<ClipRetentionItem> items,
        DateTime nowUtc,
        int retentionDays,
        int minimumLibrarySize = DefaultMinimumLibrarySize)
    {
        ArgumentNullException.ThrowIfNull(items);

        var normalizedDays = NormalizeRetentionDays(retentionDays);
        var normalizedNow = NormalizeUtc(nowUtc);
        var cutoffUtc = normalizedNow.AddDays(-normalizedDays);
        var distinctItems = items
            .Where(item => !string.IsNullOrWhiteSpace(item.FilePath))
            .GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => NormalizeUtc(item.LastWriteUtc)).First())
            .OrderByDescending(item => NormalizeUtc(item.LastWriteUtc))
            .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var alwaysKeep = distinctItems
            .Take(Math.Max(0, minimumLibrarySize))
            .Select(item => item.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var decisions = distinctItems
            .Select(item =>
            {
                var reason = ClipRetentionProtectionReason.None;
                if (item.IsFavorite)
                    reason |= ClipRetentionProtectionReason.Favorite;
                if (item.IsProjectReferenced)
                    reason |= ClipRetentionProtectionReason.ProjectReference;
                if (NormalizeUtc(item.LastWriteUtc) >= cutoffUtc)
                    reason |= ClipRetentionProtectionReason.WithinRetentionWindow;
                if (alwaysKeep.Contains(item.FilePath))
                    reason |= ClipRetentionProtectionReason.MinimumLibrarySize;
                return new ClipRetentionDecision(item, reason);
            })
            .OrderBy(decision => NormalizeUtc(decision.Item.LastWriteUtc))
            .ThenBy(decision => decision.Item.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ClipRetentionPlan
        {
            RetentionDays = normalizedDays,
            CutoffUtc = cutoffUtc,
            Decisions = decisions
        };
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
