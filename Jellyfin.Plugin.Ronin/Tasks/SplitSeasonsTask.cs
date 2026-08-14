using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;

using Jellyfin.Plugin.Ronin.Helpers;
using Jellyfin.Plugin.Ronin.Configuration;

namespace Jellyfin.Plugin.Ronin.Tasks;

/// <summary>
/// Scheduled task that normalizes anime episode season numbers using TheTVDB Aired Order.
/// </summary>
public class SplitSeasonsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger<SplitSeasonsTask> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<PluginConfiguration> _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SplitSeasonsTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager used to query media items.</param>
    /// <param name="directoryService">Service for directory operations.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="httpClientFactory">Factory for creating HTTP clients used for external API calls.</param>
    public SplitSeasonsTask(
        ILibraryManager libraryManager,
        IDirectoryService directoryService,
        ILogger<SplitSeasonsTask> logger,
        IHttpClientFactory httpClientFactory)
        : this(libraryManager, directoryService, logger, httpClientFactory, static () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SplitSeasonsTask"/> class with an explicit configuration (used by tests).
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager used to query media items.</param>
    /// <param name="directoryService">Service for directory operations.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="httpClientFactory">Factory for creating HTTP clients used for external API calls.</param>
    /// <param name="configuration">The plugin configuration to use.</param>
    internal SplitSeasonsTask(
        ILibraryManager libraryManager,
        IDirectoryService directoryService,
        ILogger<SplitSeasonsTask> logger,
        IHttpClientFactory httpClientFactory,
        PluginConfiguration configuration)
        : this(libraryManager, directoryService, logger, httpClientFactory, () => configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SplitSeasonsTask"/> class with a configuration provider.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager used to query media items.</param>
    /// <param name="directoryService">Service for directory operations.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="httpClientFactory">Factory for creating HTTP clients used for external API calls.</param>
    /// <param name="configurationProvider">Resolves the plugin configuration.</param>
    internal SplitSeasonsTask(
        ILibraryManager libraryManager,
        IDirectoryService directoryService,
        ILogger<SplitSeasonsTask> logger,
        IHttpClientFactory httpClientFactory,
        Func<PluginConfiguration> configurationProvider)
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

    /// <summary>
    /// Gets the rate limit in milliseconds applied between outbound AniDB/AnimeFillerList requests.
    /// </summary>
    private int RequestDelayMs => (Config.DbRateLimitMs > 0) ? Config.DbRateLimitMs : 2000;
    /// <summary>
    /// Gets the user preference for automatically refreshing series metadata to update the interface after changes are applied.
    /// </summary>
    private bool RefreshSeriesAfterProcessed => Config.RefreshSeriesAfterProcessed;

    /// <inheritdoc />
    public string Name => "⚠ Global Anime Re-Org: Organize in Aired Seasons";
    /// <inheritdoc />
    public string Key => "RoninSplitSeasonsTask";
    /// <inheritdoc />
    public string Description => "Redistributes episodes into seasons based on TVDB Aired Order while preserving episode numbers. Specials (Season 0) are not affected. Experimental feature; use with caution.";
    /// <inheritdoc />
    public string Category => "Ronin";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <summary>
    /// Executes the scheduled season splitting task.  
    /// For each anime series, each episode is queried against TheTVDB to determine its aired season number.
    /// </summary>
    /// <param name="progress">Reports execution progress back to Jellyfin.</param>
    /// <param name="cancellationToken">Token used to cancel execution.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting TVDB anime season splitting task");

        // Never race a library scan (same invariant as the merge task).
        if (_libraryManager.IsScanRunning)
        {
            _logger.LogWarning("A library scan is running; aborting the split run. Re-run the task after the scan completes.");
            progress?.Report(100);
            return;
        }

        var seriesList = CollectAnimeSeries.Execute(_libraryManager, Config, _logger);

        progress?.Report(0);

        var client = _httpClientFactory.CreateClient("RoninHttpClient");
        double seriesProcessed = 0;

        foreach (var series in seriesList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tvdbId = series.GetProviderId("Tvdb");
            var tvdbSlug = series.GetProviderId("TvdbSlug");

            if (string.IsNullOrEmpty(tvdbId) && string.IsNullOrEmpty(tvdbSlug))
            {
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
            .Where(e => e.GetProviderId("Tvdb") != null)
            .ToList();

            if (episodes.Count == 0)
            {
                seriesProcessed++;
                progress?.Report(seriesProcessed / seriesList.Count * 100);
                continue;
            }

            // Existing season items by number: when the target season already
            // exists we also write SeasonId/SeasonName (the server's own
            // re-home recipe); when it does not, only ParentIndexNumber is
            // written and the series refresh creates the virtual season.
            var seasonsByNumber = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Parent = series,
                IncludeItemTypes = [BaseItemKind.Season]
            })
            .Cast<Season>()
            .Where(s => s.IndexNumber.HasValue)
            .GroupBy(s => s.IndexNumber!.Value)
            .ToDictionary(g => g.Key, g => g.First());

            bool seriesModified = false;
            double episodeProcessed = 0;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var seasonAiredNumber = await ResolveSeasonNumber.AiredFromTvdbAsync(
                    tvdbId,
                    tvdbSlug,
                    episode.GetProviderId("Tvdb"),
                    client,
                    RequestDelayMs,
                    cancellationToken
                ).ConfigureAwait(false);

                var targetSeason = seasonsByNumber.TryGetValue(seasonAiredNumber, out var t) ? t : null;

                var plan = SplitPlan.Compute(
                    episode.ParentIndexNumber,
                    episode.SeasonId,
                    seasonAiredNumber,
                    targetSeason?.Id,
                    targetSeason?.Name);

                if (plan.Outcome == PlanOutcome.Update)
                {
                    _logger.LogInformation(
                        "Updating {Series} - {Episode}: Season {Old} → {New}",
                        series.Name,
                        episode.Name,
                        episode.ParentIndexNumber ?? 1,
                        seasonAiredNumber);

                    // Never ParentId — scans destructively revert it (doc E2).
                    if (plan.ParentIndexNumber.HasValue)
                    {
                        episode.ParentIndexNumber = plan.ParentIndexNumber;
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
                        await _libraryManager.UpdateItemAsync(
                            episode,
                            episode.GetParent() ?? series,
                            ItemUpdateType.MetadataEdit,
                            cancellationToken
                        ).ConfigureAwait(false);

                        seriesModified = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(
                            ex,
                            "Skipping {Series} - {Episode}: update failed",
                            series.Name,
                            episode.Name);
                    }
                }

                episodeProcessed++;
                progress?.Report((seriesProcessed + (episodeProcessed / episodes.Count)) / seriesList.Count * 100);
            }

            // Refresh the Series Metadata to update the "Season 1" counts in the UI
            if (RefreshSeriesAfterProcessed && seriesModified)
            {
                try 
                {
                    _logger.LogInformation("Refreshing metadata for series: {Series}", series.Name);
                    
                    var refreshOptions = new MetadataRefreshOptions(_directoryService)
                    {
                        MetadataRefreshMode = MetadataRefreshMode.Default,
                        ImageRefreshMode = MetadataRefreshMode.Default,
                        ReplaceAllMetadata = false,
                        ReplaceAllImages = false,
                        ForceSave = true
                    };

                    // No ValidateChildren: nothing was re-parented; the series
                    // refresh creates missing virtual seasons and runs the
                    // server's SeasonId fix-up (design doc D2.5).
                    await series.RefreshMetadata(refreshOptions, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to refresh metadata for series {Series}", series.Name);
                }
            }

            // Same rationale as the merge task: ordering must follow the
            // structure change in the same pass, now targeting aired-season
            // numbering again.
            if (Config.PlaceSpecialsAfterReorg && seriesModified)
            {
                try
                {
                    await PlaceSpecials.ExecuteForSeriesAsync(
                        _libraryManager, series, _logger, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Specials placement failed for {Series}", series.Name);
                }
            }

            seriesProcessed++;
        }

        progress?.Report(100);
    }
}