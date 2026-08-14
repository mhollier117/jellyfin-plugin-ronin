// Spec 2026-08-14 (sonarr_radarr/FINDING-ronin-specials-placement-spec.md):
// specials (S0) get AirsBefore*/AirsAfter* ordering computed from air dates,
// as a pure planner in the MergePlan/SplitPlan mould. Key rules pinned here:
//   - P1: a special with no air date is never touched (a wrong placement is
//     worse than none).
//   - P2: a series with no dated regular episodes gives no placement.
//   - P3: the target is the first regular episode airing STRICTLY after the
//     special; an equal timestamp does not count as "after" (ports the
//     validated python in scripts/330-single-season-apply.py step 5).
//   - P4/P5: merged presentation targets season 1 + absolute numbers; aired
//     presentation targets the target episode's own season/episode.
//   - P6: a special airing after every regular episode gets AirsAfter of the
//     last season (1 when merged), with AirsBefore cleared.
//   - P7: values already correct -> NoOp, so scheduled re-runs write nothing.
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class SpecialPlacementPlanTests
{
    private static readonly DateTime D1 = new(2024, 1, 7);
    private static readonly DateTime D2 = new(2024, 1, 14);
    private static readonly DateTime D3 = new(2024, 1, 21);

    private static readonly IReadOnlyList<RegularEpisodeRef> Aired = new List<RegularEpisodeRef>
    {
        new(D1, SeasonNumber: 1, IndexNumber: 1),
        new(D2, SeasonNumber: 1, IndexNumber: 2),
        new(D3, SeasonNumber: 2, IndexNumber: 1),
    };

    private static readonly IReadOnlyList<RegularEpisodeRef> Merged = new List<RegularEpisodeRef>
    {
        new(D1, SeasonNumber: 1, IndexNumber: 1),
        new(D2, SeasonNumber: 1, IndexNumber: 2),
        new(D3, SeasonNumber: 1, IndexNumber: 3),
    };

    // P1
    [Fact]
    public void NoAirDate_Skips()
    {
        var plan = SpecialPlacementPlan.Compute(
            specialAirDate: null, regulars: Aired,
            currentAirsBeforeSeason: null, currentAirsBeforeEpisode: null,
            currentAirsAfterSeason: null);

        Assert.Equal(PlanOutcome.SkipEpisode, plan.Outcome);
    }

    // P2
    [Fact]
    public void NoRegulars_Skips()
    {
        var plan = SpecialPlacementPlan.Compute(
            specialAirDate: D1, regulars: new List<RegularEpisodeRef>(),
            currentAirsBeforeSeason: null, currentAirsBeforeEpisode: null,
            currentAirsAfterSeason: null);

        Assert.Equal(PlanOutcome.SkipEpisode, plan.Outcome);
    }

    // P3 — strictly-after: a special sharing E2's timestamp lands before E3,
    // not before E2.
    [Fact]
    public void EqualTimestamp_DoesNotCountAsAfter()
    {
        var plan = SpecialPlacementPlan.Compute(
            specialAirDate: D2, regulars: Aired,
            currentAirsBeforeSeason: null, currentAirsBeforeEpisode: null,
            currentAirsAfterSeason: null);

        Assert.Equal(PlanOutcome.Update, plan.Outcome);
        Assert.Equal(2, plan.AirsBeforeSeasonNumber);
        Assert.Equal(1, plan.AirsBeforeEpisodeNumber);
        Assert.Null(plan.AirsAfterSeasonNumber);
    }

    // P4 — aired presentation: target carries its own season number.
    [Fact]
    public void AiredPresentation_TargetsAiredSeason()
    {
        var between = D1.AddDays(3);
        var plan = SpecialPlacementPlan.Compute(
            specialAirDate: between, regulars: Aired,
            currentAirsBeforeSeason: null, currentAirsBeforeEpisode: null,
            currentAirsAfterSeason: null);

        Assert.Equal(PlanOutcome.Update, plan.Outcome);
        Assert.Equal(1, plan.AirsBeforeSeasonNumber);
        Assert.Equal(2, plan.AirsBeforeEpisodeNumber);
    }

    // P5 — merged presentation: the same date targets season 1 + absolute.
    [Fact]
    public void MergedPresentation_TargetsSeasonOneAbsolute()
    {
        var between = D2.AddDays(3);
        var plan = SpecialPlacementPlan.Compute(
            specialAirDate: between, regulars: Merged,
            currentAirsBeforeSeason: null, currentAirsBeforeEpisode: null,
            currentAirsAfterSeason: null);

        Assert.Equal(PlanOutcome.Update, plan.Outcome);
        Assert.Equal(1, plan.AirsBeforeSeasonNumber);
        Assert.Equal(3, plan.AirsBeforeEpisodeNumber);
    }

    // P6 — after everything: AirsAfter last season, AirsBefore cleared.
    [Fact]
    public void AfterEveryEpisode_AirsAfterLastSeason()
    {
        var late = D3.AddDays(30);
        var plan = SpecialPlacementPlan.Compute(
            specialAirDate: late, regulars: Aired,
            currentAirsBeforeSeason: 1, currentAirsBeforeEpisode: 2,
            currentAirsAfterSeason: null);

        Assert.Equal(PlanOutcome.Update, plan.Outcome);
        Assert.Null(plan.AirsBeforeSeasonNumber);
        Assert.Null(plan.AirsBeforeEpisodeNumber);
        Assert.Equal(2, plan.AirsAfterSeasonNumber);
    }

    // P7 — idempotence: correct values -> NoOp, scheduled re-runs are free.
    [Fact]
    public void AlreadyCorrect_NoOp()
    {
        var between = D1.AddDays(3);
        var plan = SpecialPlacementPlan.Compute(
            specialAirDate: between, regulars: Aired,
            currentAirsBeforeSeason: 1, currentAirsBeforeEpisode: 2,
            currentAirsAfterSeason: null);

        Assert.Equal(PlanOutcome.NoOp, plan.Outcome);
    }
}
