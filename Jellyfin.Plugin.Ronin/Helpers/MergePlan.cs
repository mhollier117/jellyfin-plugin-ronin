using System;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Pure planner for the season merge: computes, per episode, the exact write
/// set that re-homes it into Season 1 using the server's own recipe
/// (ParentIndexNumber + SeasonId + SeasonName; never ParentId).
/// </summary>
public static class MergePlan
{
    /// <summary>
    /// Computes the merge write set for one episode.
    /// </summary>
    /// <param name="parentIndexNumber">The episode's current season number.</param>
    /// <param name="indexNumber">The episode's current episode number.</param>
    /// <param name="seasonId">The episode's current stored SeasonId.</param>
    /// <param name="seasonOneId">The series' Season 1 item id; null when the series has no Season 1 item.</param>
    /// <param name="seasonOneName">The series' Season 1 item name.</param>
    /// <param name="renumberNeeded">Whether the series' numbering is per-season (not absolute).</param>
    /// <param name="resolvedAbsoluteNumber">The externally resolved absolute number, when a lookup was performed.</param>
    /// <returns>The plan for this episode.</returns>
    public static EpisodePlan Compute(
        int? parentIndexNumber,
        int? indexNumber,
        Guid seasonId,
        Guid? seasonOneId,
        string? seasonOneName,
        bool renumberNeeded,
        int? resolvedAbsoluteNumber)
    {
        // Design doc D2.2. Specials (season 0 / unnumbered) are untouched.
        if (parentIndexNumber is null || parentIndexNumber <= 0)
        {
            return EpisodePlan.NoOpPlan;
        }

        // The Season 1 target item must exist before re-homing (doc B1):
        // the server will not create a virtual Season 1 for episodes inside
        // physical season folders, so there is nothing to re-home into.
        if (seasonOneId is null || seasonOneId.Value.Equals(Guid.Empty))
        {
            return EpisodePlan.SkipSeriesPlan;
        }

        var needsMove = parentIndexNumber != 1;
        var needsSeasonFix = !seasonId.Equals(seasonOneId.Value);

        int? newIndex = null;
        if (needsMove && renumberNeeded)
        {
            // Doc D2.3: unresolved renumbering skips the episode entirely —
            // no partial write. Merging without renumbering would collide
            // per-season numbers inside Season 1.
            if (resolvedAbsoluteNumber is not > 0)
            {
                return EpisodePlan.SkipEpisodePlan;
            }

            if (resolvedAbsoluteNumber != indexNumber)
            {
                newIndex = resolvedAbsoluteNumber;
            }
        }

        // Converged: nothing to write. The stale-SeasonId case
        // (ParentIndexNumber == 1 but SeasonId != Season 1) MUST emit an
        // update — it repairs the half-merged state 1.0.4.0 left behind and
        // removes the lethal stale AncestorIds row (doc E1.3 / U6).
        if (!needsMove && !needsSeasonFix && newIndex is null)
        {
            return EpisodePlan.NoOpPlan;
        }

        // The server's own re-home recipe (SeriesMetadataService.cs:279-292):
        // ParentIndexNumber + SeasonId + SeasonName. Never ParentId.
        return new EpisodePlan(
            PlanOutcome.Update,
            ParentIndexNumber: 1,
            SeasonId: seasonOneId,
            SeasonName: seasonOneName,
            IndexNumber: newIndex);
    }
}
