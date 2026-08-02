// Regression for the 2026-08-02 incident: LibraryIds was saved via the
// dashboard AFTER server startup, and the very next merge run still saw the
// empty list and did nothing. Jellyfin's BasePlugin.UpdateConfiguration
// REPLACES the Configuration object reference, so any snapshot taken at task
// construction (TaskManager constructs IScheduledTasks once, at boot) is
// permanently stale. Tasks must resolve configuration at execute time.
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Ronin.Configuration;
using Jellyfin.Plugin.Ronin.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public sealed class LiveConfigTests
{
    private static readonly Guid AnimeLibId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static PluginConfiguration EmptyConfig() => new() { LibraryIds = [] };

    private static PluginConfiguration ScopedConfig() => new()
    {
        LibraryIds = [AnimeLibId.ToString("N")],
        AnimeIdentificationMode = AnimeIdentificationMode.Genre,
        DbRateLimitMs = 1,
    };

    /// <summary>Stands in for Plugin.Instance: the provider returns whatever
    /// the holder currently points at, like BasePlugin.Configuration after
    /// an UpdateConfiguration swapped the object.</summary>
    private sealed class ConfigHolder
    {
        public PluginConfiguration Current { get; set; } = EmptyConfig();

        public PluginConfiguration Get() => Current;
    }

    private static Mock<ILibraryManager> EmptyLibrary(out List<InternalItemsQuery> queries)
    {
        var captured = new List<InternalItemsQuery>();
        var library = new Mock<ILibraryManager>();
        library.SetupGet(l => l.IsScanRunning).Returns(false);
        library
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback((InternalItemsQuery q) => captured.Add(q))
            .Returns([]);
        queries = captured;
        return library;
    }

    private static bool QueriedForSeries(IEnumerable<InternalItemsQuery> queries)
        => queries.Any(q => (q.IncludeItemTypes ?? []).Contains(BaseItemKind.Series));

    [Fact]
    public async Task MergeTask_HonorsConfigurationSavedAfterConstruction()
    {
        var library = EmptyLibrary(out var queries);
        var holder = new ConfigHolder();
        var task = new MergeAnimeSeasonsTask(
            library.Object,
            new Mock<IDirectoryService>().Object,
            NullLogger<MergeAnimeSeasonsTask>.Instance,
            new Mock<IHttpClientFactory>().Object,
            holder.Get);

        holder.Current = ScopedConfig(); // dashboard save lands after boot

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(QueriedForSeries(queries), "merge run ignored the configuration saved after construction");
    }

    [Fact]
    public async Task SplitTask_HonorsConfigurationSavedAfterConstruction()
    {
        var library = EmptyLibrary(out var queries);
        var holder = new ConfigHolder();
        var task = new SplitSeasonsTask(
            library.Object,
            new Mock<IDirectoryService>().Object,
            NullLogger<SplitSeasonsTask>.Instance,
            new Mock<IHttpClientFactory>().Object,
            holder.Get);

        holder.Current = ScopedConfig();

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(QueriedForSeries(queries), "split run ignored the configuration saved after construction");
    }

    [Fact]
    public async Task FillerUpdateTask_HonorsConfigurationSavedAfterConstruction()
    {
        var library = EmptyLibrary(out var queries);
        var holder = new ConfigHolder();
        var task = new FillerUpdateTask(
            library.Object,
            NullLogger<FillerUpdateTask>.Instance,
            new Mock<IHttpClientFactory>().Object,
            holder.Get);

        holder.Current = ScopedConfig();

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(QueriedForSeries(queries), "filler run ignored the configuration saved after construction");
    }
}
