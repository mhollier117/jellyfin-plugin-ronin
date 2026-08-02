namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// TRIPWIRE ONLY — Ronin no longer deletes seasons at all (as of 1.0.5.0).
///
/// History: the merge once deleted every season with IndexNumber > 1
/// unconditionally; Jellyfin 12's cascading parent-child delete removed the
/// remaining episodes with them (2026-08-01: Naruto lost 185 of 220 library
/// entries, Naruto Shippuden ~468 of 508). The 1.0.4.0 fix guarded on
/// "0 ParentId-children", but that guard is provably unsound:
/// LibraryManager.DeleteItem expands the victim set through the AncestorIds
/// table, and Episode.GetAncestorIds injects SeasonId, so episodes parented
/// elsewhere but carrying a stale SeasonId still die with the season.
/// Empty-season cleanup is therefore delegated entirely to the server's own
/// guarded RemoveObsoleteSeasons (invoked by the series refresh Ronin
/// triggers). This class is retained as a tested contract so that any future
/// re-introduction of plugin-side deletion must consciously confront it.
/// </summary>
public static class SeasonCleanup
{
    /// <summary>Decides whether a season may be safely deleted after a merge.</summary>
    /// <param name="seasonIndexNumber">The season's number; null when unnumbered.</param>
    /// <param name="childEpisodeCount">Episodes still parented to this season.</param>
    /// <param name="seasonHasPhysicalPath">Whether the season is backed by a folder on disk.</param>
    public static bool ShouldDeleteSeason(int? seasonIndexNumber, int childEpisodeCount, bool seasonHasPhysicalPath = false)
    {
        // A season backed by a folder on disk is never deletable, no matter
        // what the child count says (design doc D2.4).
        if (seasonHasPhysicalPath)
        {
            return false;
        }

        // Never touch specials (0), the merge target (1), or a season we
        // cannot reason about.
        if (seasonIndexNumber is null || seasonIndexNumber <= 1)
        {
            return false;
        }

        // The whole point: emptiness must be verified, never assumed.
        return childEpisodeCount == 0;
    }
}
