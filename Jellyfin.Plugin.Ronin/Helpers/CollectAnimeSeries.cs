using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

using Jellyfin.Plugin.Ronin.Configuration;


namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Collects anime series from the Jellyfin library based on the plugin configuration.
/// The single choke point for all four Ronin tasks: library scoping and
/// genre/tag identification are both applied here.
/// </summary>
public static class CollectAnimeSeries
{
    /// <summary>
    /// Executes the collection of anime series from the library using the
    /// active plugin configuration.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager used to query media items.</param>
    /// <param name="logger">Optional logger for scope warnings.</param>
    /// <returns>An immutable list of series identified as anime and in scope.</returns>
    public static IReadOnlyList<Series> Execute(ILibraryManager libraryManager, ILogger? logger = null)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return Execute(libraryManager, config, logger);
    }

    /// <summary>
    /// Executes the collection of anime series from the library.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager used to query media items.</param>
    /// <param name="config">The plugin configuration to apply.</param>
    /// <param name="logger">Optional logger for scope warnings.</param>
    /// <returns>An immutable list of series identified as anime and in scope.</returns>
    public static IReadOnlyList<Series> Execute(ILibraryManager libraryManager, PluginConfiguration config, ILogger? logger = null)
    {
        // Fail-safe default (design doc D3.2): no libraries selected means
        // nothing is processed. Existing installs must tick their anime
        // library once after upgrading.
        if (config.LibraryIds is null || config.LibraryIds.Length == 0)
        {
            logger?.LogWarning("Ronin: no libraries selected - nothing to do. Select libraries in the plugin settings.");
            return System.Array.Empty<Series>();
        }

        var allSeries = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [ BaseItemKind.Series ],
            Recursive = true
        })
        .Cast<Series>();

        return allSeries
            .Where(s => AnimeSeriesFilter.Matches(s.Genres, s.Tags, s.GetAncestorIds(), config))
            .ToList()
            .AsReadOnly();
    }
}
