using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Rebuilds a series' aired layout from episode FILE PATHS.
/// <para>
/// Motivated by the 2026-09-05 incident: the merge task permanently
/// self-blocks after its first partial run. <see cref="LocalOrderResolver"/>
/// derives the absolute number from the episodes in hand, but the merge
/// rewrites exactly that input - it re-homes episodes into Season 1 carrying
/// absolute numbers. Measured on a 411-episode library, Jellyfin's view
/// degraded to "S1: 174 episodes ranging 1..412, S2-S5 and S9 empty", so L3
/// ("every prior season present and contiguous from 1") failed for every
/// episode and 474 of 474 were skipped. The remote resolvers could not mask
/// it: AniDB answers 403 to its HTML scrape and the TVDB scrape returned
/// nothing.
/// </para>
/// <para>
/// The file path is the one input the merge never touches, so it still
/// carries the original layout. Rebuilding from paths restored L1/L2 and
/// resolved 410 of 411 - the single holdout being an episode whose
/// predecessor has not aired, which correctly stays unresolved.
/// </para>
/// </summary>
public static class EpisodePathLayout
{
    // Deliberately narrow: S##E## only. Loosening this to catch "1x07" or a
    // bare trailing number would start matching resolution tokens and release
    // group counters, and a WRONG season is far worse than no season - it
    // renumbers an episode onto an occupied slot, the exact collision class
    // the merge exists to avoid.
    private static readonly Regex SeasonEpisode = new(
        @"[Ss](\d{1,3})[\s._-]*[Ee](\d{1,3})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Extracts (season, episode) from an episode file path, or null.
    /// </summary>
    /// <param name="path">The episode's file path.</param>
    /// <returns>The parsed pair, or null when the file name carries no season token.</returns>
    public static (int Season, int Episode)? TryParse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // File name only. A parent directory may contain a season-looking
        // token ("Boxset S01E01 Complete") that does not describe this file.
        string name;
        try
        {
            name = Path.GetFileName(path) ?? string.Empty;
        }
        catch (System.ArgumentException)
        {
            return null;
        }

        if (name.Length == 0)
        {
            return null;
        }

        var m = SeasonEpisode.Match(name);
        if (!m.Success)
        {
            return null;
        }

        if (!int.TryParse(m.Groups[1].Value, out var season)
            || !int.TryParse(m.Groups[2].Value, out var episode))
        {
            return null;
        }

        return (season, episode);
    }

    /// <summary>
    /// Rebuilds the full layout, or null when it cannot be trusted.
    /// </summary>
    /// <param name="paths">Every episode file path in the series.</param>
    /// <returns>The layout, or null when the set is empty or any path fails to parse.</returns>
    public static IReadOnlyCollection<(int Season, int Episode)>? Build(
        IEnumerable<string?>? paths)
    {
        if (paths is null)
        {
            return null;
        }

        var layout = new List<(int Season, int Episode)>();
        foreach (var p in paths)
        {
            var parsed = TryParse(p);
            if (parsed is null)
            {
                // ALL OR NOTHING. A partial layout is worse than none: the
                // unparsed episodes would look like holes, and a hole silently
                // poisons the offset for every later season.
                return null;
            }

            layout.Add(parsed.Value);
        }

        return layout.Count == 0 ? null : layout;
    }
}
