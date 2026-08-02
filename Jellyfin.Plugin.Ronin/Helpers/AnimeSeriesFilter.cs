using System;
using System.Collections.Generic;
using System.Linq;

using Jellyfin.Plugin.Ronin.Configuration;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Pure decision: is a series an anime series that Ronin should process?
/// Library scope is checked first (fail-safe), then the configured
/// genre/tag identification mode.
/// </summary>
public static class AnimeSeriesFilter
{
    /// <summary>
    /// Determines whether a series should be processed by Ronin tasks.
    /// </summary>
    /// <param name="genres">The series' genres.</param>
    /// <param name="tags">The series' tags.</param>
    /// <param name="ancestorIds">The series' ancestor ids (includes its library folder id).</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>True when the series is in the configured library scope and matches the anime identification mode.</returns>
    public static bool Matches(
        IEnumerable<string>? genres,
        IEnumerable<string>? tags,
        IEnumerable<Guid> ancestorIds,
        PluginConfiguration config)
    {
        // Library scope gates first (fail-safe, doc D3.1): a stray "Anime"
        // tag on a series outside the selected libraries must never be enough
        // (the Alice in Borderland failure mode of 2026-08-01).
        if (!LibraryScope.IsInScope(config.LibraryIds, ancestorIds))
        {
            return false;
        }

        var hasGenre = genres?.Any(
            g => string.Equals(g, "Anime", StringComparison.OrdinalIgnoreCase)) == true;

        var targetTag = string.IsNullOrWhiteSpace(config.AnimeTargetTag) ? "Anime" : config.AnimeTargetTag;

        var hasTag = tags?.Any(
            t => string.Equals(t, targetTag, StringComparison.OrdinalIgnoreCase)) == true;

        return config.AnimeIdentificationMode switch
        {
            AnimeIdentificationMode.Genre => hasGenre,
            AnimeIdentificationMode.Tag => hasTag,
            AnimeIdentificationMode.GenreOrTag => hasGenre || hasTag,
            AnimeIdentificationMode.GenreAndTag => hasGenre && hasTag,
            _ => hasGenre
        };
    }
}
