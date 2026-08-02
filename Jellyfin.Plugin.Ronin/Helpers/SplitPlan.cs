using System;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Pure planner for the aired-season split: computes, per episode, the write
/// set that homes it into its aired season (ParentIndexNumber, plus
/// SeasonId/SeasonName when the target season item exists; never ParentId).
/// </summary>
public static class SplitPlan
{
    /// <summary>
    /// Computes the split write set for one episode.
    /// </summary>
    /// <param name="parentIndexNumber">The episode's current season number.</param>
    /// <param name="seasonId">The episode's current stored SeasonId.</param>
    /// <param name="airedSeasonNumber">The aired season number resolved from TheTVDB; values &lt;= 1 include the not-resolvable sentinel and are never acted on.</param>
    /// <param name="targetSeasonId">The id of the existing season item with that number, when present.</param>
    /// <param name="targetSeasonName">The name of that season item, when present.</param>
    /// <returns>The plan for this episode.</returns>
    public static EpisodePlan Compute(
        int? parentIndexNumber,
        Guid seasonId,
        int? airedSeasonNumber,
        Guid? targetSeasonId,
        string? targetSeasonName)
    {
        // Design doc D2.5. Aired numbers <= 1 are never acted on: 1 doubles
        // as the TVDB resolver's not-resolvable sentinel, and acting on it
        // would collapse episodes into Season 1 on lookup failure.
        if (airedSeasonNumber is not > 1)
        {
            return EpisodePlan.NoOpPlan;
        }

        var pinConverged = parentIndexNumber == airedSeasonNumber;
        var seasonConverged = targetSeasonId is null || seasonId.Equals(targetSeasonId.Value);

        if (pinConverged && seasonConverged)
        {
            return EpisodePlan.NoOpPlan;
        }

        // Same re-home recipe as the merge: ParentIndexNumber plus
        // SeasonId/SeasonName when the target season item already exists.
        // When it does not, only ParentIndexNumber is written and the series
        // refresh creates the virtual season and assigns SeasonId
        // (SeriesMetadataService.CreateSeasonsAsync).
        return new EpisodePlan(
            PlanOutcome.Update,
            ParentIndexNumber: airedSeasonNumber,
            SeasonId: targetSeasonId,
            SeasonName: targetSeasonId is null ? null : targetSeasonName);
    }
}
