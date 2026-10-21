namespace Klipp.Core.Models;

public enum CaptureNotificationKind
{
    ClipSaved,
    RecordingStarted,
    RecordingStopped,
    AutomaticSourceChange,
    RecordingBookmark,
    AudioDeviceChanged,
    CaptureProtection,
    Preview
}

public sealed record CaptureNotificationMessage(
    CaptureNotificationKind Kind, string Title, string Detail,
    DateTimeOffset CreatedAt, double DurationSeconds)
{
    public static CaptureNotificationMessage Create(CaptureNotificationKind kind, string title, string detail,
        DateTimeOffset now, double durationSeconds) => new(kind,
            Clean(title, 96), Clean(detail, 240), now,
            Math.Clamp(double.IsFinite(durationSeconds) ? durationSeconds : 4, 2, 10));

    private static string Clean(string text, int limit)
    {
        var clean = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= limit ? clean : clean[..(limit - 1)].TrimEnd() + "…";
    }
}

/// <summary>A short, bounded backlog; repeated capture events keep their newest detail.</summary>
public sealed class CaptureNotificationQueue(int capacity = 3)
{
    private readonly int _capacity = Math.Clamp(capacity, 1, 8);
    private readonly List<CaptureNotificationMessage> _pending = [];
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(15);

    public int Count => _pending.Count;

    public void Enqueue(CaptureNotificationMessage message, DateTimeOffset now)
    {
        _pending.RemoveAll(item => now - item.CreatedAt > MaximumAge);
        if (IsRecordingState(message.Kind))
            _pending.RemoveAll(item => IsRecordingState(item.Kind) && item.Kind != message.Kind);
        var existing = _pending.FindIndex(item => item.Kind == message.Kind);
        if (existing >= 0)
        {
            _pending[existing] = message;
            return;
        }
        if (_pending.Count == _capacity)
        {
            var expendable = _pending.FindIndex(item =>
                item.Kind is CaptureNotificationKind.AutomaticSourceChange or CaptureNotificationKind.Preview);
            _pending.RemoveAt(expendable >= 0 ? expendable : 0);
        }
        _pending.Add(message);
    }

    public CaptureNotificationMessage? Dequeue(DateTimeOffset now)
    {
        while (_pending.Count > 0)
        {
            var message = _pending[0];
            _pending.RemoveAt(0);
            if (now - message.CreatedAt <= MaximumAge) return message;
        }
        return null;
    }

    public void Clear() => _pending.Clear();

    public static bool ReplacesVisible(CaptureNotificationKind visible, CaptureNotificationKind incoming) =>
        visible == incoming || IsRecordingState(visible) && IsRecordingState(incoming);

    public static DateTimeOffset CoalescedDeadline(DateTimeOffset firstShown, DateTimeOffset now, double seconds)
    {
        var requested = now.AddSeconds(Math.Clamp(double.IsFinite(seconds) ? seconds : 4, 2, 10));
        var maximum = firstShown.AddSeconds(10);
        return requested < maximum ? requested : maximum;
    }

    private static bool IsRecordingState(CaptureNotificationKind kind) =>
        kind is CaptureNotificationKind.RecordingStarted or CaptureNotificationKind.RecordingStopped;
}
