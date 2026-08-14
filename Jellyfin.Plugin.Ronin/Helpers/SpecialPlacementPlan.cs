using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// A dated regular episode, as the placement planner sees it. SeasonNumber
/// and IndexNumber are the episode's CURRENT presentation values, so the
/// planner is agnostic to whether the library is merged (single-season) or
/// split (aired seasons) - it simply targets whatever numbering the target
/// episode carries right now.
/// </summary>
/// <param name="AirDate">The episode's premiere date.</param>
/// <param name="SeasonNumber">The episode's current season number.</param>
/// <param name="IndexNumber">The episode's current episode number.</param>
public sealed record RegularEpisodeRef(DateTime AirDate, int SeasonNumber, int IndexNumber);

/// <summary>
/// The computed ordering write set for one special.
/// </summary>
/// <param name="Outcome">What the caller should do.</param>
/// <param name="AirsBeforeSeasonNumber">Value to write, null clears.</param>
/// <param name="AirsBeforeEpisodeNumber">Value to write, null clears.</param>
/// <param name="AirsAfterSeasonNumber">Value to write, null clears.</param>
public sealed record SpecialPlacement(
    PlanOutcome Outcome,
    int? AirsBeforeSeasonNumber = null,
    int? AirsBeforeEpisodeNumber = null,
    int? AirsAfterSeasonNumber = null)
{
    /// <summary>A shared skip plan (special must not be touched).</summary>
    public static readonly SpecialPlacement Skip = new(PlanOutcome.SkipEpisode);
}

/// <summary>
/// Pure planner for chronological specials ordering: places a special before
/// the first regular episode that aired STRICTLY after it, or after the last
/// season when nothing did. Ports the placement validated externally in
/// sonarr_radarr/scripts/330-single-season-apply.py (spec:
/// FINDING-ronin-specials-placement-spec.md, rules P1-P7).
/// </summary>
public static class SpecialPlacementPlan
{
    /// <summary>
    /// Computes the ordering write set for one special.
    /// </summary>
    /// <param name="specialAirDate">The special's premiere date, when known.</param>
    /// <param name="regulars">All dated regular episodes of the series, any order.</param>
    /// <param name="currentAirsBeforeSeason">The special's current AirsBeforeSeasonNumber.</param>
    /// <param name="currentAirsBeforeEpisode">The special's current AirsBeforeEpisodeNumber.</param>
    /// <param name="currentAirsAfterSeason">The special's current AirsAfterSeasonNumber.</param>
    /// <returns>The placement plan.</returns>
    public static SpecialPlacement Compute(
        DateTime? specialAirDate,
        IReadOnlyList<RegularEpisodeRef> regulars,
        int? currentAirsBeforeSeason,
        int? currentAirsBeforeEpisode,
        int? currentAirsAfterSeason)
    {
        // P1/P2: without an air date on both sides there is no defensible
        // placement, and a wrong one is worse than none.
        if (!specialAirDate.HasValue || regulars.Count == 0)
        {
            return SpecialPlacement.Skip;
        }

        // P3: strictly after - an equal timestamp keeps the special AFTER
        // that episode (matches the validated python's `>` comparison).
        var target = regulars
            .Where(r => r.AirDate > specialAirDate.Value)
            .OrderBy(r => r.AirDate)
            .ThenBy(r => r.SeasonNumber)
            .ThenBy(r => r.IndexNumber)
            .FirstOrDefault();

        int? wantBeforeSeason;
        int? wantBeforeEpisode;
        int? wantAfterSeason;
        if (target is not null)
        {
            // P4/P5: target's own current numbering - correct in both the
            // merged and the aired presentation without knowing which.
            wantBeforeSeason = target.SeasonNumber;
            wantBeforeEpisode = target.IndexNumber;
            wantAfterSeason = null;
        }
        else
        {
            // P6: aired after everything.
            wantBeforeSeason = null;
            wantBeforeEpisode = null;
            wantAfterSeason = regulars.Max(r => r.SeasonNumber);
        }

        // P7: idempotence - scheduled re-runs must write nothing.
        if (currentAirsBeforeSeason == wantBeforeSeason
            && currentAirsBeforeEpisode == wantBeforeEpisode
            && currentAirsAfterSeason == wantAfterSeason)
        {
            return new SpecialPlacement(PlanOutcome.NoOp);
        }

        return new SpecialPlacement(
            PlanOutcome.Update, wantBeforeSeason, wantBeforeEpisode, wantAfterSeason);
    }
}
