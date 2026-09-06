using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

using Jellyfin.Plugin.Ronin.Helpers;
using Jellyfin.Plugin.Ronin.Configuration;

namespace Jellyfin.Plugin.Ronin.Tasks;


/// <summary>
/// Scheduled task that merges all anime seasons into a single season.
/// </summary>
public class MergeAnimeSeasonsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger<MergeAnimeSeasonsTask> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<PluginConfiguration> _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="MergeAnimeSeasonsTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Service for accessing library items.</param>
    /// <param name="directoryService">Service for directory operations.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// /// <param name="httpClientFactory">Factory for creating HTTP clients used for external API calls.</param>
    public MergeAnimeSeasonsTask(ILibraryManager libraryManager, IDirectoryService directoryService, ILogger<MergeAnimeSeasonsTask> logger, IHttpClientFactory httpClientFactory)
        : this(libraryManager, directoryService, logger, httpClientFactory, static () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MergeAnimeSeasonsTask"/> class with an explicit configuration (used by tests).
    /// </summary>
    /// <param name="libraryManager">Service for accessing library items.</param>
    /// <param name="directoryService">Service for directory operations.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="httpClientFactory">Factory for creating HTTP clients used for external API calls.</param>
    /// <param name="configuration">The plugin configuration to use.</param>
    internal MergeAnimeSeasonsTask(ILibraryManager libraryManager, IDirectoryService directoryService, ILogger<MergeAnimeSeasonsTask> logger, IHttpClientFactory httpClientFactory, PluginConfiguration configuration)
        : this(libraryManager, directoryService, logger, httpClientFactory, () => configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MergeAnimeSeasonsTask"/> class with a configuration provider.
    /// </summary>
    /// <param name="libraryManager">Service for accessing library items.</param>
    /// <param name="directoryService">Service for directory operations.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="httpClientFactory">Factory for creating HTTP clients used for external API calls.</param>
    /// <param name="configurationProvider">Resolves the plugin configuration.</param>
    internal MergeAnimeSeasonsTask(ILibraryManager libraryManager, IDirectoryService directoryService, ILogger<MergeAnimeSeasonsTask> logger, IHttpClientFactory httpClientFactory, Func<PluginConfiguration> configurationProvider)
    {
        _libraryManager = libraryManager;
        _directoryService = directoryService;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configProvider = configurationProvider;
    }

    /// <summary>Gets the live plugin configuration. Resolved through the
    /// provider on every access: BasePlugin.UpdateConfiguration replaces the
    /// Configuration object, so a value captured at construction goes stale
    /// (TaskManager constructs tasks once, at server startup).</summary>
    private PluginConfiguration Config => _configProvider();

    private bool RefreshSeriesAfterProcessed => Config.RefreshSeriesAfterProcessed;
    private int RequestDelayMs => (Config.DbRateLimitMs > 0) ? Config.DbRateLimitMs : 2000;
    private bool RenameWhenSingleSeason => Config.RenameWhenSingleSeason;
    private string SingleSeasonName => string.IsNullOrWhiteSpace(Config.SingleSeasonName) ? "Episodes" : Config.SingleSeasonName;

    /// <inheritdoc />
    public string Name => "⚠ Global Anime Re-Org: Force Single Season";
    /// <inheritdoc />
    public string Key => "RoninMergeSeasonsTask";
    /// <inheritdoc />
    public string Description => "Consolidates all episodes into a single season (season 1) for each anime series. This renumbers seasons while keeping episode metadata intact. Specials (Season 0) are untouched. Experimental feature; use with caution.";
    /// <inheritdoc />
    public string Category => "Ronin";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <summary>
    /// Executes the scheduled season merging task.  
    /// For each anime series, all episodes from multiple seasons are moved into season 1.
    /// </summary>
    /// <param name="progress">Reports execution progress back to Jellyfin.</param>
    /// <param name="cancellationToken">Token used to cancel execution.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Merge Anime Seasons Task");

        // Never race a library scan: ValidateChildren re-creating/deleting
        // rows while we write is one of the proven revert windows (doc B4).
        if (_libraryManager.IsScanRunning)
        {
            _logger.LogWarning("A library scan is running; aborting the merge run. Re-run the task after the scan completes.");
            progress?.Report(100);
            return;
        }

        var seriesList = CollectAnimeSeries.Execute(_libraryManager, Config, _logger);

        progress?.Report(0);

        if (seriesList.Count == 0)
        {
            progress?.Report(100);
            return;
        }

        var httpClient = _httpClientFactory.CreateClient("RoninHttpClient");
        var aniDbBreaker = new ScrapeCircuitBreaker();
        bool aniDbDisabledLogged = false;
        double seriesProcessed = 0;

        foreach (var series in seriesList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The Season 1 target item must exist before re-homing (doc B1):
            // the server will not create a virtual Season 1 for episodes that
            // live inside physical season folders.
            var seasons = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Parent = series,
                IncludeItemTypes = [BaseItemKind.Season]
            }).Cast<Season>().ToList();

            var seasonOne = seasons.FirstOrDefault(s => s.IndexNumber == 1);
            if (seasonOne is null)
            {
                _logger.LogWarning("Skipping {Series}: no Season 1 item to merge into", series?.Name);
                seriesProcessed++;
                progress?.Report(seriesProcessed / seriesList.Count * 100);
                continue;
            }

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Parent = series,
                IncludeItemTypes = [ BaseItemKind.Episode ],
                Recursive = true,
                IsVirtualItem = false
            })
            .Cast<Episode>()
            .Where(e => e.ParentIndexNumber.HasValue && e.ParentIndexNumber > 0)
            .ToList();

            if (episodes.Count == 0)
            {
                seriesProcessed++;
                progress?.Report(seriesProcessed / seriesList.Count * 100);
                continue;
            }

            bool seriesModified = false;
            double episodeProcessed = 0;

            // Skip external lookups entirely when the numbering is already
            // absolute: distinct and strictly increasing across seasons,
            // gaps allowed (doc D2.3).
            var numberedPairs = episodes
                .Where(e => e.IndexNumber.HasValue && e.IndexNumber > 0)
                .Select(e => (e.ParentIndexNumber!.Value, e.IndexNumber!.Value))
                .ToList();

            bool renumberNeeded = !Numbering.IsAbsoluteNumbering(numberedPairs);

            // Resolver pairs INCLUDE virtual episodes: after a first merge,
            // real prior seasons are empty (their episodes moved to season 1)
            // and the gapless guard could never pass again - but the server
            // regenerates VIRTUAL rows for merged-away seasons from provider
            // metadata, which is precisely the aired season structure the
            // local resolver needs. Locally cached, no network, provable.
            // (2026-08-14: 16 TenSura episodes were unresolvable post-merge
            // until virtual rows were admitted here.)
            var resolverPairs = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Parent = series,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true
            })
                .OfType<Episode>()
                .Where(e => e.ParentIndexNumber > 0
                            && e.IndexNumber.HasValue && e.IndexNumber > 0)
                .Select(e => (e.ParentIndexNumber!.Value, e.IndexNumber!.Value))
                .Distinct()
                .ToList();

            // Virtual rows repair the seasons the merge EMPTIED, but not the
            // season it POLLUTED. After a partial run season 1 holds the
            // merged episodes under their absolute numbers, so L3's
            // "contiguous from 1" test fails on season 1 itself and every
            // remaining episode is skipped for good. Measured 2026-09-05:
            // season 1 held 174 episodes ranging 1..412, and 474 of 474
            // Bleach episodes were skipped "absolute episode number
            // unresolved" on every run.
            //
            // The file PATH is the one input the merge never rewrites, so it
            // still describes the original aired layout. Prefer it, unioned
            // with the virtual rows so undownloaded episodes keep their slot.
            // All-or-nothing inside Build(): a partially parsed layout would
            // read as holes and poison every later offset.
            var pathPairs = EpisodePathLayout.Build(episodes.Select(e => e.Path));
            if (pathPairs is not null)
            {
                var virtualPairs = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Parent = series,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true,
                    IsVirtualItem = true
                })
                    .OfType<Episode>()
                    .Where(e => e.ParentIndexNumber > 0
                                && e.IndexNumber.HasValue && e.IndexNumber > 0)
                    .Select(e => (e.ParentIndexNumber!.Value, e.IndexNumber!.Value));

                var merged = pathPairs.Concat(virtualPairs).Distinct().ToList();
                if (merged.Count > 0)
                {
                    _logger.LogInformation(
                        "Using path-derived layout for {Series}: {Count} episode slots",
                        series?.Name,
                        merged.Count);
                    resolverPairs = merged;
                }
            }

            // Slots already occupied in season 1 - the collision guard below
            // refuses to renumber a second episode onto any of them.
            var usedAbsoluteNumbers = new HashSet<int>(
                episodes
                    .Where(e => e.ParentIndexNumber == 1 && e.IndexNumber.HasValue)
                    .Select(e => e.IndexNumber!.Value));

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int? resolvedAbsolute = null;
                if (renumberNeeded && episode.ParentIndexNumber != 1)
                {
                    // --- 1. Try TVDB first (AniDB 403-blocks this host) ---
                    if (series != null)
                    {
                        resolvedAbsolute = await ResolveEpisodeNumber.AbsoluteFromTvdbAsync(
                            series.GetProviderId("Tvdb"),
                            series.GetProviderId("TvdbSlug"),
                            episode.GetProviderId("Tvdb"),
                            httpClient,
                            RequestDelayMs,
                            cancellationToken
                        ).ConfigureAwait(false);
                    }

                    // --- 2. Fallback to AniDB behind a per-run breaker ---
                    if (!resolvedAbsolute.HasValue || resolvedAbsolute <= 0)
                    {
                        resolvedAbsolute = await ResolveEpisodeNumber.AbsoluteFromAniDbAsync(
                                episode.GetProviderId("AniDB"),
                                httpClient,
                                RequestDelayMs,
                                cancellationToken,
                                aniDbBreaker
                        ).ConfigureAwait(false);

                        if (aniDbBreaker.IsOpen && !aniDbDisabledLogged)
                        {
                            _logger.LogWarning("AniDB returned 403 Forbidden; disabling AniDB lookups for the remainder of this run");
                            aniDbDisabledLogged = true;
                        }
                    }

                    // --- 3. Network-free fallback: local aired order with a
                    // gapless guard. 2026-08-14: Solo Leveling S2 had no Tvdb
                    // episode ids and TenSura's TVDB scrapes failed - 29
                    // episodes skipped every run. When the library itself is
                    // provably gapless, the ordinal IS the absolute number.
                    if (!resolvedAbsolute.HasValue || resolvedAbsolute <= 0)
                    {
                        resolvedAbsolute = LocalOrderResolver.Compute(
                            resolverPairs,
                            episode.ParentIndexNumber ?? -1,
                            episode.IndexNumber ?? -1);
                    }
                }

                // Collision guard: never renumber onto an absolute slot
                // another episode already holds. 2026-08-14: Solo Leveling's
                // S2 episodes carried S1's AniDB ids (AniDB models sequels
                // as separate series), so a "successful" AniDB lookup would
                // have renumbered S2E1 onto S1E1 and the import would have
                // overwritten it - only the unresolved-skip saved the data.
                // Wrong remote answers must be structurally harmless.
                if (resolvedAbsolute.HasValue
                    && !usedAbsoluteNumbers.Add(resolvedAbsolute.Value))
                {
                    _logger.LogWarning(
                        "Skipping {Series} - {Episode}: resolved absolute number {Number} is already taken - remote metadata is inconsistent",
                        series?.Name,
                        episode.Name,
                        resolvedAbsolute.Value);
                    episodeProcessed++;
                    progress?.Report((seriesProcessed + (episodeProcessed / episodes.Count)) / seriesList.Count * 100);
                    continue;
                }

                var plan = MergePlan.Compute(
                    episode.ParentIndexNumber,
                    episode.IndexNumber,
                    episode.SeasonId,
                    seasonOne.Id,
                    seasonOne.Name,
                    renumberNeeded,
                    resolvedAbsolute);

                if (plan.Outcome == PlanOutcome.SkipEpisode)
                {
                    // No partial write: merging without a resolved absolute
                    // number would collide per-season numbers in Season 1.
                    // The series converges on a later run (self-healing).
                    _logger.LogWarning(
                        "Skipping {Series} - {Episode}: absolute episode number unresolved",
                        series?.Name,
                        episode.Name);
                    episodeProcessed++;
                    progress?.Report((seriesProcessed + (episodeProcessed / episodes.Count)) / seriesList.Count * 100);
                    continue;
                }

                if (plan.Outcome != PlanOutcome.Update)
                {
                    episodeProcessed++;
                    progress?.Report((seriesProcessed + (episodeProcessed / episodes.Count)) / seriesList.Count * 100);
                    continue;
                }

                _logger.LogInformation(
                    "Merging {Series} - {Episode}: Season {Old} → 1",
                    series?.Name,
                    episode.Name,
                    episode.ParentIndexNumber
                );

                // The server's own re-home recipe (SeriesMetadataService.cs:
                // 279-292): ParentIndexNumber + SeasonId + SeasonName.
                // NEVER ParentId — scans destructively revert it (doc E2).
                if (plan.ParentIndexNumber.HasValue)
                {
                    episode.ParentIndexNumber = plan.ParentIndexNumber;
                }

                if (plan.IndexNumber.HasValue)
                {
                    episode.IndexNumber = plan.IndexNumber;
                }

                if (plan.SeasonId.HasValue)
                {
                    episode.SeasonId = plan.SeasonId.Value;
                }

                if (plan.SeasonName is not null)
                {
                    episode.SeasonName = plan.SeasonName;
                }

                try
                {
                    // MetadataEdit ≥ MetadataDownload, so the NFO saver
                    // rewrites <season>1 on disk — the durability anchor that
                    // survives even Replace-All refreshes (doc E3.1). The
                    // parent argument drives folder-cache invalidation and
                    // must be the physical parent, not the episode.
                    await _libraryManager.UpdateItemAsync(
                        episode,
                        episode.GetParent() ?? seasonOne,
                        ItemUpdateType.MetadataEdit,
                        cancellationToken
                    ).ConfigureAwait(false);

                    seriesModified = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad episode must never end the run (see HttpRetry).
                    _logger.LogWarning(
                        ex,
                        "Skipping {Series} - {Episode}: update failed",
                        series?.Name,
                        episode.Name);
                }

                episodeProcessed++;
                progress?.Report((seriesProcessed + (episodeProcessed / episodes.Count)) / seriesList.Count * 100);
            }

            if (RefreshSeriesAfterProcessed && seriesModified)
            {
                try
                {
                    // === Rename Season 1 if user requested ===
                    if (RenameWhenSingleSeason
                        && !string.Equals(seasonOne.Name, SingleSeasonName, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Renaming season: {Old} → {New}", seasonOne.Name, SingleSeasonName);
                        seasonOne.Name = SingleSeasonName;

                        await _libraryManager.UpdateItemAsync(
                            seasonOne,
                            seasonOne.GetParent() ?? seasonOne,
                            ItemUpdateType.MetadataEdit,
                            cancellationToken
                        ).ConfigureAwait(false);
                    }

                    // Refresh the series so the server runs its own guarded
                    // RemoveObsoleteSeasons (virtual-season cleanup) and its
                    // end-of-refresh SeasonId fix-up. Ronin itself NEVER
                    // deletes seasons: LibraryManager.DeleteItem expands
                    // through the AncestorIds table and would take episodes
                    // with stale SeasonId references down with the season
                    // (doc E1.3 — the 2026-08-01 incident). No
                    // ValidateChildren either: nothing was re-parented, it
                    // only adds risk and time (doc D2.2).
                    _logger.LogInformation("Refreshing metadata for series: {Series}", series?.Name);

                    var refreshOptions = new MetadataRefreshOptions(_directoryService)
                    {
                        MetadataRefreshMode = MetadataRefreshMode.Default,
                        ImageRefreshMode = MetadataRefreshMode.Default,
                        ReplaceAllMetadata = false,
                        ReplaceAllImages = false,
                        ForceSave = true
                    };

                    if (series != null)
                    {
                        await series.RefreshMetadata(refreshOptions, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to refresh metadata for series {Series}", series?.Name);
                }
            }

            // Re-place specials in the same pass that changed the season
            // structure: after a merge their AirsBefore targets must use the
            // new (absolute) numbering, and doing it here means ordering is
            // never stale between a reorg and an external sweep.
            if (Config.PlaceSpecialsAfterReorg && seriesModified && series != null)
            {
                try
                {
                    await PlaceSpecials.ExecuteForSeriesAsync(
                        _libraryManager, series, _logger, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Specials placement failed for {Series}", series?.Name);
                }
            }

            seriesProcessed++;
        }

        progress?.Report(100);
        _logger.LogInformation("Finished merging all anime seasons into season 1.");
    }
}