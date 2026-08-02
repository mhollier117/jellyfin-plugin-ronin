// Design doc 2026-08-01, D2.5: the split gets the same re-home rules as the
// merge — never ParentId; write ParentIndexNumber = aired season, plus
// SeasonId/SeasonName when a season item with that number already exists
// (when it does not, only ParentIndexNumber is written and the server's
// series refresh creates the virtual season and assigns SeasonId).
// The TVDB resolver returns 1 as its not-resolvable sentinel, so aired
// numbers <= 1 are never acted on (pre-existing task behavior, kept).
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class SplitPlanTests
{
    private static readonly Guid Season1Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Season2Id = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void SplitPlan_AlreadyConverged_NoOp()
    {
        var plan = SplitPlan.Compute(
            parentIndexNumber: 2,
            seasonId: Season2Id,
            airedSeasonNumber: 2,
            targetSeasonId: Season2Id,
            targetSeasonName: "Season 2");

        Assert.Equal(PlanOutcome.NoOp, plan.Outcome);
    }

    // RED against 1.0.4.0 (split never wrote SeasonId/SeasonName).
    [Fact]
    public void SplitPlan_MovesToAiredSeason_WritesSeasonIdAndName()
    {
        var plan = SplitPlan.Compute(
            parentIndexNumber: 1,
            seasonId: Season1Id,
            airedSeasonNumber: 2,
            targetSeasonId: Season2Id,
            targetSeasonName: "Season 2");

        Assert.Equal(PlanOutcome.Update, plan.Outcome);
        Assert.Equal(2, plan.ParentIndexNumber);
        Assert.Equal(Season2Id, plan.SeasonId);
        Assert.Equal("Season 2", plan.SeasonName);
    }

    [Fact]
    public void SplitPlan_NoTargetSeasonItem_WritesOnlyParentIndexNumber()
    {
        var plan = SplitPlan.Compute(
            parentIndexNumber: 1,
            seasonId: Season1Id,
            airedSeasonNumber: 3,
            targetSeasonId: null,
            targetSeasonName: null);

        Assert.Equal(PlanOutcome.Update, plan.Outcome);
        Assert.Equal(3, plan.ParentIndexNumber);
        Assert.Null(plan.SeasonId);
        Assert.Null(plan.SeasonName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)] // 1 is also the resolver's not-resolvable sentinel
    public void SplitPlan_UnresolvedOrSeasonOne_NoOp(int? aired)
    {
        var plan = SplitPlan.Compute(
            parentIndexNumber: 2,
            seasonId: Season2Id,
            airedSeasonNumber: aired,
            targetSeasonId: Season1Id,
            targetSeasonName: "Season 1");

        Assert.Equal(PlanOutcome.NoOp, plan.Outcome);
    }

    // RED against 1.0.4.0: stale SeasonId with the right season number was
    // left half-homed forever (same gap as the merge's U6).
    [Fact]
    public void SplitPlan_StaleSeasonId_SameSeasonNumber_EmitsRepair()
    {
        var plan = SplitPlan.Compute(
            parentIndexNumber: 2,
            seasonId: Season1Id,
            airedSeasonNumber: 2,
            targetSeasonId: Season2Id,
            targetSeasonName: "Season 2");

        Assert.Equal(PlanOutcome.Update, plan.Outcome);
        Assert.Equal(Season2Id, plan.SeasonId);
        Assert.Equal("Season 2", plan.SeasonName);
    }
}
