using Klipp.Core.Models;

namespace Klipp.Tests.Models;

public sealed class OriginalClipRetentionPolicyTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OldUnprotectedClipBecomesCandidate()
    {
        var plan = OriginalClipRetentionPolicy.CreatePlan(
            [Item("old.mp4", 120)],
            NowUtc,
            retentionDays: 90,
            minimumLibrarySize: 0);

        plan.Candidates.Select(item => item.FilePath).ShouldBe(["old.mp4"]);
        plan.CandidateBytes.ShouldBe(1024);
    }

    [Fact]
    public void FavoritesAndProjectMediaAreProtected()
    {
        var plan = OriginalClipRetentionPolicy.CreatePlan(
            [
                Item("favorite.mp4", 180, favorite: true),
                Item("project.mp4", 180, projectReferenced: true)
            ],
            NowUtc,
            retentionDays: 30,
            minimumLibrarySize: 0);

        plan.Candidates.ShouldBeEmpty();
        plan.CountProtectedBy(ClipRetentionProtectionReason.Favorite).ShouldBe(1);
        plan.CountProtectedBy(ClipRetentionProtectionReason.ProjectReference).ShouldBe(1);
    }

    [Fact]
    public void MinimumLibrarySizeKeepsNewestClips()
    {
        var items = Enumerable.Range(1, 21)
            .Select(index => Item($"clip-{index:00}.mp4", 100 + index))
            .ToList();

        var plan = OriginalClipRetentionPolicy.CreatePlan(
            items,
            NowUtc,
            retentionDays: 7,
            minimumLibrarySize: 20);

        plan.Candidates.Count.ShouldBe(1);
        plan.Candidates[0].FilePath.ShouldBe("clip-21.mp4");
        plan.CountProtectedBy(ClipRetentionProtectionReason.MinimumLibrarySize).ShouldBe(20);
    }

    [Fact]
    public void ClipOnCutoffIsRetained()
    {
        var plan = OriginalClipRetentionPolicy.CreatePlan(
            [new ClipRetentionItem("cutoff.mp4", NowUtc.AddDays(-30), 1, false, false)],
            NowUtc,
            retentionDays: 30,
            minimumLibrarySize: 0);

        plan.Candidates.ShouldBeEmpty();
        plan.CountProtectedBy(ClipRetentionProtectionReason.WithinRetentionWindow).ShouldBe(1);
    }

    [Fact]
    public void DuplicatePathsAreEvaluatedOnce()
    {
        var plan = OriginalClipRetentionPolicy.CreatePlan(
            [
                Item("same.mp4", 120),
                Item("SAME.mp4", 150)
            ],
            NowUtc,
            retentionDays: 30,
            minimumLibrarySize: 0);

        plan.Decisions.Count.ShouldBe(1);
        plan.Candidates.Count.ShouldBe(1);
        plan.Candidates[0].LastWriteUtc.ShouldBe(NowUtc.AddDays(-120));
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(30, 30)]
    [InlineData(9999, 3650)]
    public void RetentionDaysAreNormalized(int requested, int expected)
    {
        OriginalClipRetentionPolicy.NormalizeRetentionDays(requested).ShouldBe(expected);
    }

    private static ClipRetentionItem Item(
        string path,
        int ageDays,
        bool favorite = false,
        bool projectReferenced = false) =>
        new(path, NowUtc.AddDays(-ageDays), 1024, favorite, projectReferenced);
}
