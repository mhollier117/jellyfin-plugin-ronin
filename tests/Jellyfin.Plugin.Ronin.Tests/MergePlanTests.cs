// Design doc 2026-08-01, D2.2 / tests U5-U9, U12: the merge write set is
// computed by a pure planner mirroring the server's own re-home recipe
// (SeriesMetadataService.cs:279-292): ParentIndexNumber=1 + SeasonId +
// SeasonName — never ParentId. Key regressions pinned here:
//   - U6: an episode with ParentIndexNumber==1 but a stale SeasonId MUST be
//     repaired (1.0.4.0 skipped it; the stale AncestorIds row it leaves is
//     what made the 2026-08-01 season deletions lethal).
//   - U12: unresolved renumbering skips the episode entirely — merging
//     without renumbering creates duplicate "Episode 1" entries in Season 1.
//   - U9: a series with no Season 1 item is skipped (no re-home target).
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class MergePlanTests
{
    private static readonly Guid Season1Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Season2Id = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Doc U5.
    [Fact]
    public void MergePlan_AlreadyConverged_NoOp()
    {
        var plan = MergePlan.Compute(
            parentIndexNumber: 1,
            indexNumber: 5,
            seasonId: Season1Id,
            seasonOneId: Season1Id,
            seasonOneName: "Episodes",
            renumberNeeded: false,
            resolvedAbsoluteNumber: null);

        Assert.Equal(PlanOutcome.NoOp, plan.Outcome);
    }

    // Doc U6 — THE incident-regression test. RED against 1.0.4.0 semantics.
    [Fact]
    public void MergePlan_StaleSeasonId_EmitsUpdate()
    {
        var plan = MergePlan.Compute(
            parentIndexNumber: 1,
            indexNumber: 5,
            seasonId: Season2Id,
            seasonOneId: Season1Id,
            seasonOneName: "Episodes",
            renumberNeeded: false,
            resolvedAbsoluteNumber: null);

        Assert.Equal(PlanOutcome.Update, plan.Outcome);
        Assert.Equal(Season1Id, plan.SeasonId);
        Assert.Equal("Episodes", plan.SeasonName);
        Assert.Null(plan.IndexNumber); // number untouched
    }

    // Doc U7.
    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void MergePlan_Specials_Untouched(int? parentIndexNumber)
    {
        var plan = MergePlan.Compute(
            parentIndexNumber,
            indexNumber: 3,
            seasonId: Season2Id,
            seasonOneId: Season1Id,
            seasonOneName: "Episodes",
            renumberNeeded: true,
            resolvedAbsoluteNumber: null);

        Assert.Equal(PlanOutcome.NoOp, plan.Outcome);
    }

    // Doc U8 — RED against 1.0.4.0 (SeasonId/SeasonName were never written).
    [Fact]
    public void MergePlan_SetsSeasonIdAndName_WithParentIndexNumber()
    {
        var plan = MergePlan.Compute(
            parentIndexNumber: 2,
            indexNumber: 3,
            seasonId: Season2Id,
            seasonOneId: Season1Id,
            seasonOneName: "Season 1",
            renumberNeeded: false,
            resolvedAbsoluteNumber: null);

        Assert.Equal(PlanOutcome.Update, plan.Outcome);
        Assert.Equal(1, plan.ParentIndexNumber);
        Assert.Equal(Season1Id, plan.SeasonId);
        Assert.Equal("Season 1", plan.SeasonName);
        Assert.Null(plan.IndexNumber); // numbering already absolute
    }

    // Doc U9 — RED against 1.0.4.0 (it merged regardless).
    [Fact]
    public void MergePlan_NoSeasonOne_SkipsSeries()
    {
        var plan = MergePlan.Compute(
            parentIndexNumber: 2,
            indexNumber: 3,
            seasonId: Season2Id,
            seasonOneId: null,
            seasonOneName: null,
            renumberNeeded: false,
            resolvedAbsoluteNumber: null);

        Assert.Equal(PlanOutcome.SkipSeries, plan.Outcome);
    }

    // Doc U12 — RED against 1.0.4.0 (merged without renumbering).
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void MergePlan_RenumberUnresolved_SkipsEpisodeEntirely(int? resolved)
    {
        var plan = MergePlan.Compute(
            parentIndexNumber: 2,
            indexNumber: 1,
            seasonId: Season2Id,
            seasonOneId: Season1Id,
            seasonOneName: "Episodes",
            renumberNeeded: true,
            resolvedAbsoluteNumber: resolved);

        Assert.Equal(PlanOutcome.SkipEpisode, plan.Outcome);
        Assert.Null(plan.ParentIndexNumber); // no partial write
        Assert.Null(plan.SeasonId);
        Assert.Null(plan.IndexNumber);
    }

    [Fact]
    public void MergePlan_RenumberResolved_WritesAbsoluteNumber()
    {
        var plan = MergePlan.Compute(
            parentIndexNumber: 2,
            indexNumber: 1,
            seasonId: Season2Id,
            seasonOneId: Season1Id,
            seasonOneName: "Episodes",
            renumberNeeded: true,
            resolvedAbsoluteNumber: 26);

        Assert.Equal(PlanOutcome.Update, plan.Outcome);
        Assert.Equal(1, plan.ParentIndexNumber);
        Assert.Equal(26, plan.IndexNumber);
        Assert.Equal(Season1Id, plan.SeasonId);
        Assert.Equal("Episodes", plan.SeasonName);
    }

    // Converged episode in a per-season-numbered series: Season 1 episodes'
    // numbers are already the absolute ones; nothing to write.
    [Fact]
    public void MergePlan_ConvergedSeasonOneEpisode_RenumberSeriesWide_NoOp()
    {
        var plan = MergePlan.Compute(
            parentIndexNumber: 1,
            indexNumber: 7,
            seasonId: Season1Id,
            seasonOneId: Season1Id,
            seasonOneName: "Episodes",
            renumberNeeded: true,
            resolvedAbsoluteNumber: null);

        Assert.Equal(PlanOutcome.NoOp, plan.Outcome);
    }
}
