using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Applies chronological ordering to a series' specials using the pure
/// <see cref="SpecialPlacementPlan"/> planner. Shared by the standalone
/// scheduled task and the post-reorg hooks in the merge/split tasks, so
/// ordering is recomputed in the same pass that changes season structure and
/// is never stale between a reorg and an external sweep.
///
/// Air dates come from the items' own PremiereDate, which the configured
/// metadata providers (AniDB/TVDB) already populated - no scraping, no rate
/// limits, and a special without a date is skipped untouched (spec P1).
/// </summary>
public static class PlaceSpecials
{
    /// <summary>
    /// Recomputes ordering for every dated special of one series.
    /// </summary>
    /// <param name="libraryManager">Service for accessing library items.</param>
    /// <param name="series">The series to process.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of specials whose ordering was updated.</returns>
    public static async Task<int> ExecuteForSeriesAsync(
        ILibraryManager libraryManager,
        Series series,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var episodes = libraryManager.GetItemList(new InternalItemsQuery
        {
            Parent = series,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
        }).OfType<Episode>().ToList();

        var regulars = episodes
            .Where(e => e.ParentIndexNumber is > 0
                        && e.IndexNumber.HasValue
                        && e.PremiereDate.HasValue)
            .Select(e => new RegularEpisodeRef(
                e.PremiereDate!.Value,
                e.ParentIndexNumber!.Value,
                e.IndexNumber!.Value))
            .ToList();

        var updated = 0;
        foreach (var special in episodes.Where(e => e.ParentIndexNumber == 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = SpecialPlacementPlan.Compute(
                special.PremiereDate,
                regulars,
                special.AirsBeforeSeasonNumber,
                special.AirsBeforeEpisodeNumber,
                special.AirsAfterSeasonNumber);
            if (plan.Outcome != PlanOutcome.Update)
            {
                continue;
            }

            logger.LogInformation(
                "Placing special {Series} - {Special}: before S{Season}E{Episode} / after S{After}",
                series.Name,
                special.Name,
                plan.AirsBeforeSeasonNumber,
                plan.AirsBeforeEpisodeNumber,
                plan.AirsAfterSeasonNumber);
            special.AirsBeforeSeasonNumber = plan.AirsBeforeSeasonNumber;
            special.AirsBeforeEpisodeNumber = plan.AirsBeforeEpisodeNumber;
            special.AirsAfterSeasonNumber = plan.AirsAfterSeasonNumber;
            try
            {
                await libraryManager.UpdateItemAsync(
                    special,
                    special.GetParent() ?? series,
                    ItemUpdateType.MetadataEdit,
                    cancellationToken).ConfigureAwait(false);
                updated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // skip-and-log, never fail the run (the 1.0.0.1-era lesson)
                logger.LogWarning(
                    ex, "Skipping special {Series} - {Special}: update failed", series.Name, special.Name);
            }
        }

        return updated;
    }
}
