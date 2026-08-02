// Design doc 2026-08-01, D2.1/D2.4 / tests U14, U16, U17 + task-level scoping.
// Safety invariants of the merge task, asserted against a mocked
// ILibraryManager wired into BaseItem's static service properties:
//   - U14: a merge run NEVER invokes ILibraryManager.DeleteItem — on
//     anything. The 1.0.4.0 "provably childless" guard counted only
//     ParentId-children; LibraryManager.DeleteItem expands through the
//     AncestorIds table (Episode.GetAncestorIds injects SeasonId), so a
//     season reading 0 children could still cascade-delete episodes.
//     Observed 2026-08-01: Naruto lost 185 of 220 entries, Shippuden ~468.
//   - U16: UpdateItemAsync receives the episode's physical parent (the
//     parent argument drives folder-cache invalidation; passing the episode
//     itself made it a no-op).
//   - U17: a run aborts up front while a library scan is running (writing
//     while ValidateChildren races is one of the proven revert windows).
//   - Scoping: with an empty LibraryIds config nothing is processed; a series
//     outside the configured libraries is never touched.
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Ronin.Configuration;
using Jellyfin.Plugin.Ronin.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LibraryTaskScheduler;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

[Collection("BaseItemStatics")]
public sealed class MergeTaskSafetyTests
{
    private static readonly Guid AnimeLibId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid OtherLibId = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private sealed class InlineScheduler : ILimitedConcurrencyLibraryScheduler
    {
        public async Task Enqueue<T>(T[] data, Func<T, IProgress<double>, Task> worker, IProgress<double> progress, CancellationToken cancellationToken)
        {
            foreach (var item in data)
            {
                await worker(item, new Progress<double>()).ConfigureAwait(false);
            }
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent(string.Empty)
            });
        }
    }

    private sealed class Rig
    {
        public Mock<ILibraryManager> Library { get; } = new();
        public Mock<IItemRepository> Repository { get; } = new();
        public Mock<IProviderManager> Provider { get; } = new();
        public Mock<IDirectoryService> Directory { get; } = new();
        public Mock<IFileSystem> FileSystem { get; } = new();
        public Mock<IServerConfigurationManager> Config { get; } = new();
        public CountingHandler Handler { get; } = new();
        public Mock<IHttpClientFactory> HttpFactory { get; } = new();

        public Series Series { get; }
        public Season Season1 { get; }
        public Season Season2 { get; }
        public Season Season3 { get; }
        public List<Episode> Episodes { get; }
        public Folder LibraryFolder { get; }

        public List<(BaseItem Item, BaseItem Parent, ItemUpdateType Reason)> Updates { get; } = new();

        public Rig(Guid collectionFolderId, bool scanRunning = false)
        {
            LibraryFolder = new Folder { Id = collectionFolderId, Name = "Anime" };

            Series = new Series { Id = Guid.NewGuid(), Name = "Test Series", Genres = ["Anime"] };
            Season1 = new Season { Id = Guid.NewGuid(), Name = "Season 1", IndexNumber = 1, ParentId = Series.Id, SeriesId = Series.Id };
            Season2 = new Season { Id = Guid.NewGuid(), Name = "Season 2", IndexNumber = 2, ParentId = Series.Id, SeriesId = Series.Id };
            Season3 = new Season { Id = Guid.NewGuid(), Name = "Season 3", IndexNumber = 3, ParentId = Series.Id, SeriesId = Series.Id };

            // Absolute, gapless numbering: no scraping needed under either
            // heuristic. Season 3 exists but holds no episodes (the shape the
            // 1.0.4.0 guard deleted). Season 2 keeps its episodes ParentId-
            // chained (the merge never touches ParentId).
            Episodes =
            [
                MakeEpisode(1, 1, Season1),
                MakeEpisode(1, 2, Season1),
                MakeEpisode(2, 3, Season2),
                MakeEpisode(2, 4, Season2),
            ];

            BaseItem.LibraryManager = Library.Object;
            BaseItem.ItemRepository = Repository.Object;
            BaseItem.ProviderManager = Provider.Object;
            BaseItem.FileSystem = FileSystem.Object;
            BaseItem.Logger = NullLogger<BaseItem>.Instance;
            BaseItem.ConfigurationManager = Config.Object;
            BaseItem.MediaSourceManager = new Mock<MediaBrowser.Controller.Library.IMediaSourceManager>().Object;
            Video.RecordingsManager = new Mock<MediaBrowser.Controller.LiveTv.IRecordingsManager>().Object;
            Folder.LimitedConcurrencyLibraryScheduler = new InlineScheduler();

            Config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());
            Provider
                .Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ItemUpdateType.None);

            Library.SetupGet(l => l.IsScanRunning).Returns(scanRunning);
            Library
                .Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>()))
                .Returns([LibraryFolder]);
            Library
                .Setup(l => l.UpdateImagesAsync(It.IsAny<BaseItem>(), It.IsAny<bool>()))
                .Returns(Task.CompletedTask);
            Library
                .Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
                .Callback((BaseItem item, BaseItem parent, ItemUpdateType reason, CancellationToken _) => Updates.Add((item, parent, reason)))
                .Returns(Task.CompletedTask);
            Library
                .Setup(l => l.GetItemById(It.IsAny<Guid>()))
                .Returns((Guid id) => AllItems().FirstOrDefault(i => i.Id.Equals(id)));
            Library
                .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns((InternalItemsQuery q) => Route(q));
            Repository
                .Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns((InternalItemsQuery q) => Route(q));

            HttpFactory
                .Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(Handler));
        }

        private static Episode MakeEpisode(int seasonNumber, int episodeNumber, Season season)
            => new()
            {
                Id = Guid.NewGuid(),
                Name = $"Episode S{seasonNumber}E{episodeNumber}",
                ParentIndexNumber = seasonNumber,
                IndexNumber = episodeNumber,
                ParentId = season.Id,
                SeasonId = season.Id,
                SeasonName = season.Name,
            };

        private IEnumerable<BaseItem> AllItems()
            => new BaseItem[] { LibraryFolder, Series, Season1, Season2, Season3 }.Concat(Episodes);

        private IReadOnlyList<BaseItem> Route(InternalItemsQuery q)
        {
            var kinds = q.IncludeItemTypes ?? [];
            var parentIsSeries = q.ParentId.Equals(Series.Id);
            var parentSeason = new[] { Season1, Season2, Season3 }.FirstOrDefault(s => s.Id.Equals(q.ParentId));

            if (kinds.Contains(BaseItemKind.Series))
            {
                return [Series];
            }

            if (kinds.Contains(BaseItemKind.Season) && parentIsSeries)
            {
                return [Season1, Season2, Season3];
            }

            if (kinds.Contains(BaseItemKind.Episode))
            {
                if (parentIsSeries)
                {
                    return Episodes.Cast<BaseItem>().ToList();
                }

                if (parentSeason is not null)
                {
                    return Episodes.Where(e => e.ParentId.Equals(parentSeason.Id)).Cast<BaseItem>().ToList();
                }
            }

            if (kinds.Length == 0 && parentIsSeries)
            {
                return [Season1, Season2, Season3];
            }

            if (kinds.Length == 0 && parentSeason is not null)
            {
                return Episodes.Where(e => e.ParentId.Equals(parentSeason.Id)).Cast<BaseItem>().ToList();
            }

            return [];
        }

        public MergeAnimeSeasonsTask CreateTask(PluginConfiguration config)
            => new(
                Library.Object,
                Directory.Object,
                NullLogger<MergeAnimeSeasonsTask>.Instance,
                HttpFactory.Object,
                config);

        public void VerifyNoDeletions()
        {
            Library.Verify(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never());
            Library.Verify(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<bool>()), Times.Never());
            Library.Verify(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<BaseItem>(), It.IsAny<bool>()), Times.Never());
            Library.Verify(l => l.DeleteItemsUnsafeFast(It.IsAny<IReadOnlyCollection<BaseItem>>(), It.IsAny<bool>()), Times.Never());
        }
    }

    private static PluginConfiguration ScopedConfig(bool refreshAfter = true) => new()
    {
        LibraryIds = [AnimeLibId.ToString("N")],
        AnimeIdentificationMode = AnimeIdentificationMode.Genre,
        RefreshSeriesAfterProcessed = refreshAfter,
        RenameWhenSingleSeason = true,
        DbRateLimitMs = 1,
    };

    // Doc U14 — RED against 1.0.4.0 (it deleted "childless" Season 3).
    [Fact]
    public async Task MergeTask_NeverCallsDeleteItem()
    {
        var rig = new Rig(AnimeLibId);
        var task = rig.CreateTask(ScopedConfig(refreshAfter: true));

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        rig.VerifyNoDeletions();
        Assert.Contains(rig.Updates, u => u.Item is Episode); // the merge itself did run
    }

    // Doc U16 — RED against 1.0.4.0 (episode was passed as its own parent).
    [Fact]
    public async Task MergeTask_UpdateItemAsync_ReceivesEpisodePhysicalParent()
    {
        var rig = new Rig(AnimeLibId);
        var task = rig.CreateTask(ScopedConfig(refreshAfter: false));

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var episodeUpdates = rig.Updates.Where(u => u.Item is Episode).ToList();
        Assert.NotEmpty(episodeUpdates);
        Assert.All(episodeUpdates, u =>
        {
            Assert.IsNotType<Episode>(u.Parent);
            Assert.Equal(rig.Season2.Id, u.Parent.Id); // only Season 2 episodes move
        });
    }

    // Doc U17 — RED against 1.0.4.0 (no scan guard existed).
    [Fact]
    public async Task MergeTask_AbortsWhenScanRunning()
    {
        var rig = new Rig(AnimeLibId, scanRunning: true);
        var task = rig.CreateTask(ScopedConfig());

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Empty(rig.Updates);
        rig.VerifyNoDeletions();
    }

    // Doc D3.2 — empty config processes nothing. RED against 1.0.4.0.
    [Fact]
    public async Task MergeTask_EmptyLibraryConfig_ProcessesNothing()
    {
        var rig = new Rig(AnimeLibId);
        var config = ScopedConfig();
        config.LibraryIds = [];
        var task = rig.CreateTask(config);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Empty(rig.Updates);
        rig.VerifyNoDeletions();
    }

    // Doc D3.1 — a series outside the configured library is never touched.
    // RED against 1.0.4.0 (the Alice in Borderland failure mode).
    [Fact]
    public async Task MergeTask_OutOfScopeSeries_Untouched()
    {
        var rig = new Rig(OtherLibId); // series lives in a different library
        var task = rig.CreateTask(ScopedConfig());

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Empty(rig.Updates);
        rig.VerifyNoDeletions();
    }

    // Sequential-skip at task level: absolute gapless numbering means the
    // merge must not send a single scrape request.
    [Fact]
    public async Task MergeTask_SequentialNumbering_NoScrapeRequests()
    {
        var rig = new Rig(AnimeLibId);
        var task = rig.CreateTask(ScopedConfig(refreshAfter: false));

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(0, rig.Handler.Attempts);
        Assert.Contains(rig.Updates, u => u.Item is Episode);
    }
}
