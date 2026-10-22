using Klipp.Core.Updates;

namespace Klipp.Tests.Updates;

public sealed class AppUpdateActivityTests
{
    [Fact]
    public void Idle_app_allows_a_quiet_download_and_restart()
    {
        var activity = new AppUpdateActivity();

        activity.CanDownloadAutomatically.ShouldBeTrue();
        activity.RestartBlockedReason.ShouldBeNull();
    }

    public static IEnumerable<object[]> BusyCombinations =>
        Enumerable.Range(1, 255).Select(mask => new object[] { mask });

    [Theory]
    [MemberData(nameof(BusyCombinations))]
    public void Every_busy_combination_defers_download_and_explains_why_restart_is_blocked(int mask)
    {
        var activity = new AppUpdateActivity(
            Recording: (mask & 1) != 0,
            InstantClips: (mask & 2) != 0,
            Exporting: (mask & 4) != 0,
            Editing: (mask & 8) != 0,
            FileOperation: (mask & 16) != 0,
            CaptureChanging: (mask & 32) != 0,
            Maintenance: (mask & 64) != 0,
            Playback: (mask & 128) != 0);

        activity.CanDownloadAutomatically.ShouldBeFalse();
        activity.RestartBlockedReason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Restart_reason_prioritizes_capture_then_exports_and_file_operations()
    {
        var activity = new AppUpdateActivity(true, true, true, true, true, true, true);
        activity.RestartBlockedReason!.ShouldContain("Stop recording");

        activity = activity with { Recording = false };
        activity.RestartBlockedReason!.ShouldContain("Pause instant clips");

        activity = activity with { InstantClips = false };
        activity.RestartBlockedReason!.ShouldContain("exports");

        activity = activity with { Exporting = false };
        activity.RestartBlockedReason!.ShouldContain("file operations");

        activity = activity with { FileOperation = false };
        activity.RestartBlockedReason!.ShouldContain("capture source");

        activity = activity with { CaptureChanging = false };
        activity.RestartBlockedReason!.ShouldContain("Return to the library");

        activity = activity with { Editing = false };
        activity.RestartBlockedReason!.ShouldContain("storage maintenance");

        activity = activity with { Maintenance = false };
        activity.RestartBlockedReason.ShouldBeNull();
        activity.CanDownloadAutomatically.ShouldBeTrue();
    }

    [Fact]
    public void Finishing_one_task_does_not_hide_another_busy_task()
    {
        var activity = new AppUpdateActivity(Recording: true, Exporting: true);

        activity = activity with { Recording = false };

        activity.CanDownloadAutomatically.ShouldBeFalse();
        activity.RestartBlockedReason!.ShouldContain("exports");
    }

    [Fact]
    public void Returning_from_the_editor_does_not_authorize_restart_over_active_playback()
    {
        var activity = new AppUpdateActivity(Editing: true, Playback: true);

        activity = activity with { Editing = false };

        activity.CanDownloadAutomatically.ShouldBeFalse();
        activity.RestartBlockedReason!.ShouldContain("Close video playback");
        activity = activity with { Playback = false };
        activity.RestartBlockedReason.ShouldBeNull();
    }
}
