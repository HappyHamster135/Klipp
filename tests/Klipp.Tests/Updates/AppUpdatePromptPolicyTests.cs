using Klipp.Core.Updates;

namespace Klipp.Tests.Updates;

public sealed class AppUpdatePromptPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Nothing_is_shown_without_a_verified_ready_version(string? version)
    {
        var policy = new AppUpdatePromptPolicy();

        policy.ShouldShow(version, canShow: true).ShouldBeFalse();
        policy.ShouldShow("1.0.9", canShow: true).ShouldBeTrue();
    }

    [Fact]
    public void A_ready_update_waits_for_the_full_ui_safety_gate()
    {
        var policy = new AppUpdatePromptPolicy();

        // Ready can arrive during recording, editing, playback, a dialog, or
        // while the app is in the tray. None of those polls consumes it.
        for (var poll = 0; poll < 8; poll++)
            policy.ShouldShow("1.0.9", canShow: false).ShouldBeFalse();

        policy.ShouldShow("1.0.9", canShow: true).ShouldBeTrue();
    }

    [Fact]
    public void Starting_work_hides_the_card_without_losing_the_pending_update()
    {
        var policy = new AppUpdatePromptPolicy();

        policy.ShouldShow("1.0.9", canShow: true).ShouldBeTrue();
        policy.ShouldShow("1.0.9", canShow: false).ShouldBeFalse();
        policy.ShouldShow("1.0.9", canShow: true).ShouldBeTrue();
    }

    [Fact]
    public void Refreshing_a_visible_card_does_not_toggle_it_or_consume_it()
    {
        var policy = new AppUpdatePromptPolicy();

        for (var poll = 0; poll < 8; poll++)
            policy.ShouldShow("1.0.9", canShow: true).ShouldBeTrue();
    }

    [Fact]
    public void Later_prevents_repeated_prompts_when_activity_or_focus_changes()
    {
        var policy = new AppUpdatePromptPolicy();
        policy.ShouldShow("1.0.9", canShow: true).ShouldBeTrue();

        policy.Defer("1.0.9");

        policy.ShouldShow("1.0.9", canShow: true).ShouldBeFalse();
        policy.ShouldShow("1.0.9", canShow: false).ShouldBeFalse();
        policy.ShouldShow("1.0.9", canShow: true).ShouldBeFalse();
    }

    [Fact]
    public void Losing_and_restoring_ready_state_does_not_forget_Later()
    {
        var policy = new AppUpdatePromptPolicy();
        policy.Defer("1.0.9");

        policy.ShouldShow(null, canShow: true).ShouldBeFalse();
        policy.ShouldShow("1.0.9", canShow: true).ShouldBeFalse();
    }

    [Fact]
    public void A_newer_download_can_be_announced_after_deferring_an_older_update()
    {
        var policy = new AppUpdatePromptPolicy();
        policy.Defer("1.0.9");

        policy.ShouldShow("1.0.10", canShow: false).ShouldBeFalse();
        policy.ShouldShow("1.0.10", canShow: true).ShouldBeTrue();
        policy.ShouldShow("1.0.9", canShow: true).ShouldBeFalse();
    }

    [Fact]
    public void Deferring_another_version_does_not_reenable_an_earlier_dismissal()
    {
        var policy = new AppUpdatePromptPolicy();
        policy.Defer("1.0.9");
        policy.Defer("1.0.10");

        policy.ShouldShow("1.0.9", canShow: true).ShouldBeFalse();
        policy.ShouldShow("1.0.10", canShow: true).ShouldBeFalse();
        policy.ShouldShow("1.0.11", canShow: true).ShouldBeTrue();
    }

    [Fact]
    public void A_saved_deferral_can_be_restored_when_the_app_reopens()
    {
        var policy = new AppUpdatePromptPolicy("1.0.9");

        policy.ShouldShow("1.0.9", canShow: true).ShouldBeFalse();
        policy.ShouldShow("1.0.10", canShow: true).ShouldBeTrue();
    }

    [Fact]
    public void Session_only_deferral_does_not_change_another_policy_instance()
    {
        var firstSession = new AppUpdatePromptPolicy();
        firstSession.Defer("1.0.9");

        var newSession = new AppUpdatePromptPolicy();

        newSession.ShouldShow("1.0.9", canShow: true).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void An_empty_deferral_cannot_dismiss_a_future_update(string? version)
    {
        var policy = new AppUpdatePromptPolicy();
        policy.Defer(version);

        policy.ShouldShow("1.0.9", canShow: true).ShouldBeTrue();
    }

    [Fact]
    public void Recording_and_editing_activity_keep_a_ready_update_pending()
    {
        var policy = new AppUpdatePromptPolicy();
        var recording = new AppUpdateActivity(Recording: true);
        var editing = new AppUpdateActivity(Editing: true);
        var idle = new AppUpdateActivity();

        policy.ShouldShow("1.0.9", recording.CanDownloadAutomatically).ShouldBeFalse();
        policy.ShouldShow("1.0.9", editing.CanDownloadAutomatically).ShouldBeFalse();
        policy.ShouldShow("1.0.9", idle.CanDownloadAutomatically).ShouldBeTrue();
    }
}
