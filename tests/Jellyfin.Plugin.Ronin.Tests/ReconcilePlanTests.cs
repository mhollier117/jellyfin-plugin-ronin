// 2026-09-06: MergePlan sets needsMove = ParentIndexNumber != 1, so an episode
// already in season 1 is a no-op and is never re-examined. Numbers assigned by
// the pre-API resolvers therefore persist forever, even once an authoritative
// source exists to correct them. A library-wide audit against TheTVDB found
// 2,312 correct and 15 wrong:
//
//   World Trigger  S03E01..E14  numbered 98..111, TVDB says 86..99  (+12)
//   Mushoku Tensei S03E03       numbered 50,      TVDB says 52      (-2)
//
// The Mushoku Tensei one also blocked convergence: a wrong incumbent held slot
// 50, which is the slot TVDB assigns to an episode still waiting to merge.
//
// Renumbering existing content is the exact operation the collision guard
// exists to prevent, so this planner is deliberately all-or-nothing: it either
// produces a provably conflict-free layout for the whole series, or it declines
// and changes nothing.
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class ReconcilePlanTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static Dictionary<(int, int), int> Map(params (int s, int e, int abs)[] rows)
    {
        var d = new Dictionary<(int, int), int>();
        foreach (var r in rows) d[(r.s, r.e)] = r.abs;
        return d;
    }

    [Fact]
    public void CorrectsAnEpisodeThatDisagreesWithTheAuthority()
    {
        // The World Trigger shape: numbered 98, TVDB says 86.
        var plan = ReconcilePlan.Compute(
            new[] { new ReconcileItem(A, 3, 1, 98) },
            Map((3, 1, 86)));
        Assert.Single(plan);
        Assert.Equal((A, 86), plan[0]);
    }

    [Fact]
    public void LeavesCorrectEpisodesAlone()
    {
        var plan = ReconcilePlan.Compute(
            new[] { new ReconcileItem(A, 1, 1, 1), new ReconcileItem(B, 1, 2, 2) },
            Map((1, 1, 1), (1, 2, 2)));
        Assert.Empty(plan);
    }

    [Fact]
    public void DecliesEntirelyWhenTheResultWouldCollide()
    {
        // Two episodes cannot occupy one absolute slot. Rather than pick a
        // winner, change nothing - a half-applied renumber is worse than none.
        var plan = ReconcilePlan.Compute(
            new[] { new ReconcileItem(A, 1, 1, 5), new ReconcileItem(B, 1, 2, 6) },
            Map((1, 1, 9), (1, 2, 9)));
        Assert.Empty(plan);
    }

    [Fact]
    public void DeclinesWhenAMoveWouldLandOnAnUntouchableIncumbent()
    {
        // C is not in the map, so it keeps 86. Moving A onto 86 would collide.
        var plan = ReconcilePlan.Compute(
            new[] { new ReconcileItem(A, 3, 1, 98), new ReconcileItem(C, 9, 9, 86) },
            Map((3, 1, 86)));
        Assert.Empty(plan);
    }

    [Fact]
    public void ASwapIsSafeBecauseTheFinalLayoutIsStillUnique()
    {
        // A:50->52 and B:52->50. Intermediate states collide but the final
        // layout is a clean bijection, and the caller writes the whole plan.
        var plan = ReconcilePlan.Compute(
            new[] { new ReconcileItem(A, 3, 3, 50), new ReconcileItem(B, 3, 5, 52) },
            Map((3, 3, 52), (3, 5, 50)));
        Assert.Equal(2, plan.Count);
        Assert.Contains((A, 52), plan);
        Assert.Contains((B, 50), plan);
    }

    [Fact]
    public void EpisodesAbsentFromTheMapAreNeverGuessedAt()
    {
        // No authority for this slot means no change. The whole point is to
        // stop using derived numbers.
        var plan = ReconcilePlan.Compute(
            new[] { new ReconcileItem(A, 4, 7, 123) },
            Map((1, 1, 1)));
        Assert.Empty(plan);
    }

    [Fact]
    public void AnEmptyAuthorityMapChangesNothing()
    {
        var plan = ReconcilePlan.Compute(
            new[] { new ReconcileItem(A, 1, 1, 99) },
            new Dictionary<(int, int), int>());
        Assert.Empty(plan);
    }

    [Fact]
    public void SpecialsAreNeverReconciled()
    {
        var plan = ReconcilePlan.Compute(
            new[] { new ReconcileItem(A, 0, 1, 4) },
            Map((0, 1, 99)));
        Assert.Empty(plan);
    }

    [Fact]
    public void EpisodesWithNoCurrentNumberAreStillCorrectable()
    {
        var plan = ReconcilePlan.Compute(
            new[] { new ReconcileItem(A, 2, 3, null) },
            Map((2, 3, 30)));
        Assert.Single(plan);
        Assert.Equal((A, 30), plan[0]);
    }

    [Fact]
    public void NullOrEmptyInputIsHandled()
    {
        Assert.Empty(ReconcilePlan.Compute(null!, Map((1, 1, 1))));
        Assert.Empty(ReconcilePlan.Compute(Array.Empty<ReconcileItem>(), Map((1, 1, 1))));
        Assert.Empty(ReconcilePlan.Compute(new[] { new ReconcileItem(A, 1, 1, 1) }, null!));
    }

    [Fact]
    public void DuplicateIdsDoNotProduceTwoWrites()
    {
        var plan = ReconcilePlan.Compute(
            new[] { new ReconcileItem(A, 1, 1, 5), new ReconcileItem(A, 1, 1, 5) },
            Map((1, 1, 7)));
        Assert.Single(plan);
    }
}
