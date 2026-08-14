using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Computes an episode's absolute number from the episodes already in the
/// library, with a gapless guard - the network-free fallback for when remote
/// resolution fails or cannot be trusted. Motivated by the 2026-08-14
/// incident: Solo Leveling S2 had no Tvdb episode ids and carried S1's AniDB
/// ids (AniDB models sequels as separate series), so the only SAFE sources
/// were "give up" or the local aired order. This resolver answers only when
/// the local order provably matches the aired order: every season before the
/// target contiguous from 1, and the target's season contiguous from 1
/// through the target episode. Any gap returns null - a guess would renumber
/// an episode onto the wrong slot, which is exactly the collision class the
/// merge exists to avoid.
/// </summary>
public static class LocalOrderResolver
{
    /// <summary>
    /// Computes the absolute number for one episode, or null when the local
    /// library cannot prove it.
    /// </summary>
    /// <param name="episodes">All (season, episode) pairs of the series; specials (season 0) are ignored.</param>
    /// <param name="targetSeason">The target episode's season number.</param>
    /// <param name="targetEpisode">The target episode's episode number.</param>
    /// <returns>The absolute number, or null.</returns>
    public static int? Compute(
        IReadOnlyCollection<(int Season, int Episode)> episodes,
        int targetSeason,
        int targetEpisode)
    {
        if (targetSeason < 1 || targetEpisode < 1)
        {
            return null;
        }

        // L4: season 1 is its own absolute numbering.
        if (targetSeason == 1)
        {
            return targetEpisode;
        }

        var bySeason = episodes
            .Where(e => e.Season > 0)
            .GroupBy(e => e.Season)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Episode).ToHashSet());

        var offset = 0;
        for (var season = 1; season < targetSeason; season++)
        {
            // L3: a prior season missing entirely, or with any hole from 1
            // to its maximum, makes the offset unknowable.
            if (!bySeason.TryGetValue(season, out var eps) || eps.Count == 0)
            {
                return null;
            }

            var max = eps.Max();
            if (eps.Count != max || Enumerable.Range(1, max).Any(n => !eps.Contains(n)))
            {
                return null;
            }

            offset += max;
        }

        // L2: the target's own season must be contiguous from 1 through the
        // target episode. L5: gaps after it cannot shift its position.
        if (!bySeason.TryGetValue(targetSeason, out var own)
            || Enumerable.Range(1, targetEpisode).Any(n => !own.Contains(n)))
        {
            return null;
        }

        return offset + targetEpisode;
    }
}
