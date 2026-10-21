using Klipp.Core.Models;

namespace Klipp.Tests.Models;

public sealed class CaptureNotificationQueueTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
    private static CaptureNotificationMessage Message(CaptureNotificationKind kind, string detail = "one") =>
        CaptureNotificationMessage.Create(kind, "Saved", detail, Now, 4);

    [Fact]
    public void RapidSourceChangesKeepOnlyTheLatestDetail()
    {
        var queue = new CaptureNotificationQueue();
        for (var index = 0; index < 100; index++)
            queue.Enqueue(Message(CaptureNotificationKind.AutomaticSourceChange, index.ToString()), Now);
        queue.Count.ShouldBe(1);
        queue.Dequeue(Now)!.Detail.ShouldBe("99");
        queue.Dequeue(Now).ShouldBeNull();
    }

    [Fact]
    public void BacklogIsBoundedAndPrefersSavedEventsOverSourceChatter()
    {
        var queue = new CaptureNotificationQueue(3);
        queue.Enqueue(Message(CaptureNotificationKind.ClipSaved), Now);
        queue.Enqueue(Message(CaptureNotificationKind.AutomaticSourceChange), Now);
        queue.Enqueue(Message(CaptureNotificationKind.RecordingStarted), Now);
        queue.Enqueue(Message(CaptureNotificationKind.Preview), Now);
        queue.Count.ShouldBe(3);
        queue.Dequeue(Now)!.Kind.ShouldBe(CaptureNotificationKind.ClipSaved);
        queue.Dequeue(Now)!.Kind.ShouldBe(CaptureNotificationKind.RecordingStarted);
        queue.Dequeue(Now)!.Kind.ShouldBe(CaptureNotificationKind.Preview);
    }

    [Fact]
    public void StoppedRecordingSupersedesAQueuedStartedMessage()
    {
        var queue = new CaptureNotificationQueue();
        queue.Enqueue(Message(CaptureNotificationKind.RecordingStarted), Now);
        queue.Enqueue(Message(CaptureNotificationKind.RecordingStopped), Now);
        queue.Count.ShouldBe(1);
        queue.Dequeue(Now)!.Kind.ShouldBe(CaptureNotificationKind.RecordingStopped);
        CaptureNotificationQueue.ReplacesVisible(CaptureNotificationKind.RecordingStarted,
            CaptureNotificationKind.RecordingStopped).ShouldBeTrue();
        CaptureNotificationQueue.ReplacesVisible(CaptureNotificationKind.ClipSaved,
            CaptureNotificationKind.AutomaticSourceChange).ShouldBeFalse();
    }

    [Fact]
    public void OldQueuedEventsDoNotAppearAfterTheirContextHasPassed()
    {
        var queue = new CaptureNotificationQueue();
        queue.Enqueue(Message(CaptureNotificationKind.AutomaticSourceChange), Now);
        queue.Dequeue(Now.AddSeconds(16)).ShouldBeNull();
        queue.Enqueue(Message(CaptureNotificationKind.ClipSaved), Now.AddSeconds(16));
        queue.Enqueue(CaptureNotificationMessage.Create(CaptureNotificationKind.RecordingStarted,
            "Recording", "new", Now.AddSeconds(16), 4), Now.AddSeconds(16));
        queue.Count.ShouldBe(1);
        queue.Dequeue(Now.AddSeconds(16))!.Detail.ShouldBe("new");
    }

    [Fact]
    public void RepeatedVisibleEventsCannotExtendAPopupBeyondTenSeconds()
    {
        CaptureNotificationQueue.CoalescedDeadline(Now, Now.AddSeconds(8), 4).ShouldBe(Now.AddSeconds(10));
        CaptureNotificationQueue.CoalescedDeadline(Now, Now.AddSeconds(1), 4).ShouldBe(Now.AddSeconds(5));
    }

    [Theory]
    [InlineData(-10, 2)]
    [InlineData(30, 10)]
    [InlineData(double.NaN, 4)]
    public void DurationAndDetailAreSafeForTheCompactPopup(double requested, double expected)
    {
        var message = CaptureNotificationMessage.Create(CaptureNotificationKind.ClipSaved,
            " Saved\r\n", new string('x', 500), Now, requested);
        message.DurationSeconds.ShouldBe(expected);
        message.Title.ShouldBe("Saved");
        message.Detail.Length.ShouldBe(240);
        message.Detail.ShouldEndWith("…");
    }
}
