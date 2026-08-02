using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Ronin.Configuration;
using Jellyfin.Plugin.Ronin.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Ronin.Api;

/// <summary>
/// Answers which of the given item ids are display-empty seasons that the
/// web UI should hide (merged look without touching files or the database).
/// </summary>
[ApiController]
[Route("Ronin")]
public class EmptySeasonsController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmptySeasonsController"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager used to query media items.</param>
    public EmptySeasonsController(ILibraryManager libraryManager) => _libraryManager = libraryManager;

    /// <summary>
    /// Filters the supplied item ids down to seasons that should be hidden:
    /// in-scope (library allow-list), index above 1, and referenced by no
    /// episode's SeasonId. Ids that are not seasons are ignored.
    /// </summary>
    /// <param name="ids">Comma-separated item ids as sent by the web client.</param>
    /// <returns>The subset of ids to hide, in the client's "N" format.</returns>
    [HttpGet("HiddenSeasons")]
    [Authorize]
    public ActionResult<IReadOnlyList<string>> HiddenSeasons([FromQuery] string? ids)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.HideEmptySeasons || string.IsNullOrWhiteSpace(ids))
        {
            return Ok(Array.Empty<string>());
        }

        var seasons = ids
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Select(_libraryManager.GetItemById)
            .OfType<Season>()
            .Where(s => LibraryScope.IsInScope(config.LibraryIds, s.GetAncestorIds()))
            .ToList();

        var hidden = new List<string>();
        foreach (var group in seasons.GroupBy(s => s.SeriesId))
        {
            var series = _libraryManager.GetItemById(group.Key);
            if (series is null)
            {
                continue;
            }

            // Virtual placeholders are intentionally included: a season whose
            // only content is missing-episode placeholders still has a view.
            var episodeSeasonIds = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Parent = series,
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true
            })
            .OfType<Episode>()
            .Select(e => e.SeasonId);

            hidden.AddRange(EmptySeasons
                .Compute(group.Select(s => (s.Id, s.IndexNumber)), episodeSeasonIds)
                .Select(g => g.ToString("N")));
        }

        return Ok(hidden);
    }
}
