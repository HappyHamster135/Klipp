using Klipp.Core.Updates;

namespace Klipp.Tests.Updates;

public sealed class AppUpdateReleaseNotesTests
{
    [Fact]
    public void Authored_changes_are_preferred_to_the_title_and_intro()
    {
        var notes = """
            # Klipp 1.0.10
            Here is the next release.
            - **Preview changes** before restarting.
              - A technical implementation detail.
            - Open [full release notes](https://example.test/notes).
            - Keep `recording` &amp; editing uninterrupted.
            - A fourth change belongs in the full notes.
            """;

        AppUpdateReleaseNotes.Summarize(notes).ShouldBe([
            "Preview changes before restarting.", "Open full release notes.", "Keep recording & editing uninterrupted."]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("# Version\n\n---\n![image](https://example.test/picture.png)")]
    public void Missing_notes_do_not_invent_highlights(string? notes) =>
        AppUpdateReleaseNotes.Summarize(notes).ShouldBeEmpty();

    [Fact]
    public void Code_examples_are_not_release_highlights()
    {
        AppUpdateReleaseNotes.Summarize("```text\n- example only\n```\n- Faster playback.\n- Faster playback.")
            .ShouldBe(["Faster playback."]);
    }

    [Fact]
    public void Plain_paragraph_notes_are_readable_without_html_or_hidden_controls()
    {
        AppUpdateReleaseNotes.Summarize("<b>Clearer</b> controls\u202E\u0000 and\t better sound.")
            .ShouldBe(["Clearer controls and better sound."]);
    }

    [Fact]
    public void Large_notes_have_bounded_highlights_and_preserve_readable_words()
    {
        var result = AppUpdateReleaseNotes.Summarize("- " + string.Concat(Enumerable.Repeat("Smoother playback and editing. ", 10_000)));

        result.Count.ShouldBe(1);
        result[0].Length.ShouldBeLessThanOrEqualTo(AppUpdateReleaseNotes.MaximumHighlightLength);
        result[0].ShouldEndWith("…");
        result[0].ShouldNotContain("\n");
    }

    [Fact]
    public void Hard_limits_do_not_split_an_emoji()
    {
        var result = AppUpdateReleaseNotes.Summarize("- " + new string('x', 98) + "🎬" + new string('y', 20));

        result[0].ShouldBe(new string('x', 98) + "…");
    }

    [Fact]
    public void Numbered_lists_and_checked_changes_are_supported()
    {
        AppUpdateReleaseNotes.Summarize("1. [x] Better audio.\n2. [ ] Cleaner controls.")
            .ShouldBe(["Better audio.", "Cleaner controls."]);
    }
}
