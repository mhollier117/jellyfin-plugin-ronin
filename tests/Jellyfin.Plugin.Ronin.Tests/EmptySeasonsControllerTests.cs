// Reproduces the production shape observed 2026-08-02: merged series where
// every real episode's SeasonId points at Season 1 while empty Season 2+
// items (physical folder-backed or stale virtual) remain. The endpoint must
// flag those seasons. Mirrors the MergeTaskSafetyTests rig: mocked
// ILibraryManager wired into BaseItem's static service properties so
// GetAncestorIds()/GetParents() work like the server's.
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Ronin.Api;
using Jellyfin.Plugin.Ronin.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

[Collection("BaseItemStatics")]
public sealed class EmptySeasonsControllerTests
{
    private static readonly Guid AnimeLibId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private sealed class Rig
    {
        public Mock<ILibraryManager> Library { get; } = new();

        public Folder LibraryFolder { get; }

        public Series Series { get; }

        public Season Season1 { get; }

        public Season Season2 { get; }

        public Season Season3 { get; }

        public List<Episode> Episodes { get; }

        public Rig()
        {
            LibraryFolder = new Folder { Id = AnimeLibId, Name = "Anime" };
            Series = new Series { Id = Guid.NewGuid(), Name = "Merged Show", ParentId = LibraryFolder.Id };
            Season1 = new Season { Id = Guid.NewGuid(), Name = "Episodes", IndexNumber = 1, ParentId = Series.Id, SeriesId = Series.Id };
            Season2 = new Season { Id = Guid.NewGuid(), Name = "Season 2", IndexNumber = 2, ParentId = Series.Id, SeriesId = Series.Id };
            Season3 = new Season { Id = Guid.NewGuid(), Name = "Season 3", IndexNumber = 3, ParentId = Series.Id, SeriesId = Series.Id };

            // The merged state: files may still sit in season folders
            // (ParentId = Season2) but SeasonId re-homes them to Season 1.
            Episodes =
            [
                new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = 1, ParentId = Season1.Id, SeasonId = Season1.Id },
                new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = 2, ParentId = Season2.Id, SeasonId = Season1.Id },
            ];

            BaseItem.LibraryManager = Library.Object;
            BaseItem.ConfigurationManager = Mock.Of<IServerConfigurationManager>(c => c.Configuration == new ServerConfiguration());
            BaseItem.Logger = NullLogger<BaseItem>.Instance;

            Library
                .Setup(l => l.GetItemById(It.IsAny<Guid>()))
                .Returns((Guid id) => AllItems().FirstOrDefault(i => i.Id.Equals(id)));
            Library
                .Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>()))
                .Returns([LibraryFolder]);
            Library
                .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns((InternalItemsQuery q) =>
                    q.IncludeItemTypes.Contains(BaseItemKind.Episode)
                        ? Episodes.Cast<BaseItem>().ToList()
                        : []);
        }

        public EmptySeasonsController CreateController(PluginConfiguration config)
            => new(Library.Object, NullLogger<EmptySeasonsController>.Instance, () => config);

        private IEnumerable<BaseItem> AllItems()
            => new BaseItem[] { LibraryFolder, Series, Season1, Season2, Season3 }.Concat(Episodes);
    }

    private static PluginConfiguration ScopedConfig() => new()
    {
        LibraryIds = [AnimeLibId.ToString("N")],
        HideEmptySeasons = true,
    };

    private static IReadOnlyList<string> Invoke(EmptySeasonsController controller, params Guid[] ids)
    {
        var result = controller.HiddenSeasons(string.Join(',', ids.Select(g => g.ToString("N"))));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(ok.Value);
    }

    [Fact]
    public void FlagsUnreferencedSeasonsInMergedSeries()
    {
        var rig = new Rig();
        var controller = rig.CreateController(ScopedConfig());

        var hidden = Invoke(controller, rig.Season1.Id, rig.Season2.Id, rig.Season3.Id);

        Assert.Equal(
            new[] { rig.Season2.Id.ToString("N"), rig.Season3.Id.ToString("N") },
            hidden);
    }

    [Fact]
    public void OutOfScopeLibraryReturnsNothing()
    {
        var rig = new Rig();
        var config = ScopedConfig();
        config.LibraryIds = [Guid.NewGuid().ToString("N")];
        var controller = rig.CreateController(config);

        Assert.Empty(Invoke(controller, rig.Season2.Id));
    }

    [Fact]
    public void DisabledFeatureReturnsNothing()
    {
        var rig = new Rig();
        var config = ScopedConfig();
        config.HideEmptySeasons = false;
        var controller = rig.CreateController(config);

        Assert.Empty(Invoke(controller, rig.Season2.Id));
    }
}
