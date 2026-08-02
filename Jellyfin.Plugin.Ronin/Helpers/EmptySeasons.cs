using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Computes which season rows are display-empty and safe to hide in the UI.
/// After the self-healing merge, episodes are re-homed via SeasonId while
/// their files (and therefore ParentId) stay in the physical "Season NN"
/// folders. Those folder-backed season items cannot be deleted - scans
/// re-create them from disk, and deleting them was the 1.0.4.0 data-loss
/// bug - so the merged look is produced by hiding them client-side instead.
/// </summary>
public static class EmptySeasons
{
    /// <summary>
    /// Returns the ids of seasons that no episode references through
    /// SeasonId. Season 1, Specials (index 0) and seasons without an index
    /// are never hidden. Virtual placeholder episodes count as content.
    /// </summary>
    /// <param name="seasons">Season id and index number pairs for one series.</param>
    /// <param name="episodeSeasonIds">SeasonId of every episode in the series, virtual episodes included.</param>
    /// <returns>Ids of display-empty seasons, in input order.</returns>
    public static IReadOnlyList<Guid> Compute(
        IEnumerable<(Guid Id, int? IndexNumber)> seasons,
        IEnumerable<Guid> episodeSeasonIds)
    {
        var referenced = new HashSet<Guid>(episodeSeasonIds);
        return seasons
            .Where(s => s.IndexNumber is > 1 && !referenced.Contains(s.Id))
            .Select(s => s.Id)
            .ToList();
    }
}
