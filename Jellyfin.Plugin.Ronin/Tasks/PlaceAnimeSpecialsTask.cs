using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ronin.Configuration;
using Jellyfin.Plugin.Ronin.Helpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ronin.Tasks;

/// <summary>
/// Scheduled task that orders each anime series' specials chronologically
/// between the regular episodes, by air date. Works in both presentation
/// states (merged single-season or aired seasons) because placement targets
/// whatever numbering the neighbouring episode currently carries. Also
/// invoked per-series by the merge/split tasks after a reorg when
/// <see cref="PluginConfiguration.PlaceSpecialsAfterReorg"/> is enabled.
/// </summary>
public class PlaceAnimeSpecialsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PlaceAnimeSpecialsTask> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaceAnimeSpecialsTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Service for accessing library items.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public PlaceAnimeSpecialsTask(ILibraryManager libraryManager, ILogger<PlaceAnimeSpecialsTask> logger)
        : this(libraryManager, logger, static () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaceAnimeSpecialsTask"/> class with a configuration provider (used by tests).
    /// </summary>
    /// <param name="libraryManager">Service for accessing library items.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="configurationProvider">Provider for the plugin configuration.</param>
    internal PlaceAnimeSpecialsTask(ILibraryManager libraryManager, ILogger<PlaceAnimeSpecialsTask> logger, Func<PluginConfiguration> configurationProvider)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _configProvider = configurationProvider;
    }

    /// <inheritdoc />
    public string Name => "Order Anime Specials Chronologically";

    /// <inheritdoc />
    public string Key => "RoninPlaceSpecialsTask";

    /// <inheritdoc />
    public string Description => "Sets AirsBefore/AirsAfter on specials so they queue between the episodes they aired around. Specials without air dates are left untouched.";

    /// <inheritdoc />
    public string Category => "Ronin";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Place Anime Specials Task");
        var seriesList = CollectAnimeSeries.Execute(_libraryManager, _configProvider(), _logger);
        progress?.Report(0.0);
        var processed = 0.0;
        var total = 0;
        foreach (var series in seriesList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += await PlaceSpecials.ExecuteForSeriesAsync(
                _libraryManager, series, _logger, cancellationToken).ConfigureAwait(false);
            processed++;
            progress?.Report(processed / seriesList.Count * 100.0);
        }

        _logger.LogInformation("Placed {Count} special(s) across {Series} series", total, seriesList.Count);
        progress?.Report(100.0);
    }
}
