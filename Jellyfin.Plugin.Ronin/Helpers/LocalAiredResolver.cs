using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// The inverse of <see cref="LocalOrderResolver"/>: recovers the aired
/// (season, episode) for an ABSOLUTE episode number by walking season sizes
/// already known to the library (real + virtual rows - virtual rows are the
/// server's cached provider structure, so this needs no network and no
/// per-episode provider ids). Refuses to walk through any season whose
/// contiguous prefix ends before its highest present index: sizes past a
/// hole are unknowable, and a guess would re-home an episode onto the wrong
/// aired slot (rules R1-R6 in LocalAiredResolverTests).
/// </summary>
public static class LocalAiredResolver
{
    /// <summary>
    /// Computes the aired (season, episode) for one absolute number, or
    /// null when the local structure cannot prove it.
    /// </summary>
    /// <param name="episodes">All (season, episode) pairs of the series, real and virtual; specials (season 0) are ignored.</param>
    /// <param name="absoluteNumber">The absolute episode number to place.</param>
    /// <returns>The aired pair, or null.</returns>
    public static (int Season, int Episode)? Compute(
        IReadOnlyCollection<(int Season, int Episode)> episodes,
        int absoluteNumber)
    {
        if (absoluteNumber < 1)
        {
            return null;
        }

        var bySeason = episodes
            .Where(e => e.Season > 0 && e.Episode > 0)
            .GroupBy(e => e.Season)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Episode).ToHashSet());
        if (bySeason.Count == 0)
        {
            return null;
        }

        var cum = 0;
        for (var season = 1; season <= bySeason.Keys.Max(); season++)
        {
            if (!bySeason.TryGetValue(season, out var eps) || eps.Count == 0)
            {
                return null;                     // season absent entirely
            }

            var prefix = 0;
            while (eps.Contains(prefix + 1))
            {
                prefix++;
            }

            if (absoluteNumber <= cum + prefix)
            {
                return (season, absoluteNumber - cum);
            }

            // Walking THROUGH this season requires its full size - a hole
            // between the contiguous prefix and the max present index makes
            // that unknowable (R4).
            if (prefix < eps.Max())
            {
                return null;
            }

            cum += prefix;
        }

        return null;                             // beyond all known seasons
    }
}
