using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Pure helpers deciding whether a series' episode numbering is already
/// absolute (unique across seasons), in which case no external lookups are
/// needed during a merge.
/// </summary>
public static class Numbering
{
    /// <summary>
    /// Determines whether the given (season, episode) pairs already form an
    /// absolute numbering.
    /// </summary>
    /// <param name="numberedEpisodes">One pair per episode that has both a
    /// season number (&gt; 0) and an episode number (&gt; 0).</param>
    /// <returns>True when numbering is absolute and no renumbering (and no
    /// scraping) is required.</returns>
    public static bool IsAbsoluteNumbering(IReadOnlyCollection<(int Season, int Episode)> numberedEpisodes)
    {
        // Design doc D2.3: numbering is absolute when episode numbers are
        // distinct and strictly increasing ordered by (season, episode) —
        // gaps allowed, because missing episodes are normal in this library.
        // (The 1.0.4.0 heuristic demanded a gapless 1..N run, which forced
        // pointless scraping on any series with a missing episode.)
        var ordered = numberedEpisodes
            .OrderBy(p => p.Season)
            .ThenBy(p => p.Episode)
            .Select(p => p.Episode)
            .ToList();

        if (ordered.Count == 0)
        {
            return false;
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i] <= ordered[i - 1])
            {
                return false;
            }
        }

        return true;
    }
}
