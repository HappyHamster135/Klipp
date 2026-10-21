namespace Klipp.Core.Editing;

public sealed record EditorLinkedTrimPlan(IReadOnlySet<string> ClipIds, double EdgeDelta);

/// <summary>Trims only explicitly selected, aligned source partners using one source-safe edge delta.</summary>
public static class EditorLinkedTrimMath
{
    public static EditorLinkedTrimPlan Plan(string editedId, double requestedDelta, bool leading,
        IEnumerable<EditorRippleSourceSpan> selected)
    {
        var spans = selected.ToList();
        var edited = spans.FirstOrDefault(span => span.Id == editedId);
        if (edited is null) return new(new HashSet<string>(), 0);
        var sourceId = edited.IsVideo ? edited.Id : edited.SourceClipId;
        bool Aligned(EditorRippleSourceSpan span) =>
            Math.Abs(span.TimelineStart - edited.TimelineStart) < .000001 &&
            Math.Abs(span.Duration - edited.Duration) < .000001 &&
            Math.Abs(span.Speed - edited.Speed) < .000001 &&
            Math.Abs(span.TrimStart - edited.TrimStart) < .000001 &&
            Math.Abs(span.TrimEnd - edited.TrimEnd) < .000001;
        var family = spans.Where(span => span.Id == edited.Id || !string.IsNullOrEmpty(sourceId) &&
            (span.Id == sourceId || span.SourceClipId == sourceId) && Aligned(span)).ToList();
        var delta = EditorSelectionEditMath.ClampTrimDelta(requestedDelta, leading, family.Select(span =>
            new EditorSelectionTrimRange(span.TimelineStart, span.TrimStart, span.TrimEnd, span.SourceDuration, span.Speed)));
        return new(family.Select(span => span.Id).ToHashSet(StringComparer.Ordinal), delta);
    }
}
