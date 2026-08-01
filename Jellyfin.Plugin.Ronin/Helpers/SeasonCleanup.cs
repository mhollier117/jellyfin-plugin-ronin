namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Guards season deletion during the merge.
///
/// Why this exists: the merge previously deleted every season with
/// IndexNumber > 1 unconditionally, assuming the episodes had already moved.
/// When any episode had not moved, Jellyfin 12's cascading parent-child delete
/// removed it along with the season. Observed 2026-08-01: Naruto lost 185 of
/// 220 library entries, Naruto Shippuden ~468 of 508.
///
/// A season is only ever removed when it is provably childless.
/// </summary>
public static class SeasonCleanup
{
    /// <summary>Decides whether a season may be safely deleted after a merge.</summary>
    /// <param name="seasonIndexNumber">The season's number; null when unnumbered.</param>
    /// <param name="childEpisodeCount">Episodes still parented to this season.</param>
    public static bool ShouldDeleteSeason(int? seasonIndexNumber, int childEpisodeCount)
    {
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
