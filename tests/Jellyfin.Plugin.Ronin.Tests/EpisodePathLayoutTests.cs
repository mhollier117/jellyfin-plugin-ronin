// 2026-09-05 incident: the merge task PERMANENTLY self-blocks after its first
// partial run. LocalOrderResolver derives the absolute number from the
// episodes in hand, but the merge itself rewrites exactly that input - it
// re-homes episodes into Season 1 carrying absolute numbers. Measured on a
// 411-episode Bleach library:
//
//   jellyfin's view after a partial run   paths on disk (never rewritten)
//     S1  174 eps, range 1..412  GAPS       S1  20 eps 1..20   contiguous
//     S2..S5, S9  EMPTY (merged away)       S2  21 eps 1..21   contiguous
//     S6  20 eps, range 3..22    GAPS       ...
//     S17 10 eps, range 31..42   GAPS       S16 24 eps 1..24   contiguous
//
// L3 ("every season before the target present and contiguous from 1") then
// fails for every episode: S1 has Count 174 != max 412, and S2-S5/S9 are
// gone. 474 of 474 Bleach episodes skipped "absolute episode number
// unresolved". The remote resolvers could not mask it either - AniDB answers
// 403 Forbidden to its HTML scrape, and the TVDB scrape produced nothing.
//
// The file PATH is the one input the merge never touches, so it still carries
// the original aired layout. Rebuilding the layout from paths restores L1/L2
// and resolves 410 of 411 (S17E46 alone stays unresolved, because E45 has not
// aired - which is correct, not a regression).
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class EpisodePathLayoutTests
{
    [Theory]
    [InlineData(@"D:\Anime\Bleach (2004)\Bleach (2004) - S07E07 - Hueco Mondo.mkv", 7, 7)]
    [InlineData(@"D:\Anime\Bleach\Bleach - s01e01 - Pilot.mkv", 1, 1)]
    [InlineData(@"D:\A\Show - S1E1 - Title.mkv", 1, 1)]
    [InlineData(@"D:\A\Show - S017E123 - Title.mkv", 17, 123)]
    [InlineData(@"/mnt/anime/Show/Show - S02E15 - Name.mkv", 2, 15)]
    public void ParsesSeasonEpisodeFromFilename(string path, int season, int episode)
    {
        var got = EpisodePathLayout.TryParse(path);
        Assert.NotNull(got);
        Assert.Equal(season, got!.Value.Season);
        Assert.Equal(episode, got.Value.Episode);
    }

    [Theory]
    // Absolute-named libraries carry no season token. Those series do not need
    // renumbering at all, so returning null (and falling back) is correct.
    [InlineData(@"D:\Anime\Bleach\Bleach - 138 - Title.mkv")]
    [InlineData(@"D:\Anime\Bleach\138.mkv")]
    [InlineData(@"D:\Anime\Bleach\Bleach Movie 2.mkv")]
    [InlineData("")]
    [InlineData(null)]
    public void ReturnsNullWhenFilenameHasNoSeasonToken(string? path)
        => Assert.Null(EpisodePathLayout.TryParse(path));

    [Fact]
    public void UsesFilenameNotDirectory()
    {
        // A directory may contain a season-looking token; only the file name
        // describes the episode.
        var got = EpisodePathLayout.TryParse(
            @"D:\Anime\Boxset S01E01 Complete\Show - S04E09 - Title.mkv");
        Assert.NotNull(got);
        Assert.Equal(4, got!.Value.Season);
        Assert.Equal(9, got.Value.Episode);
    }

    [Fact]
    public void SeasonZeroIsParsedAndLeftForTheCallerToIgnore()
    {
        var got = EpisodePathLayout.TryParse(@"D:\A\Show - S00E05 - Special.mkv");
        Assert.NotNull(got);
        Assert.Equal(0, got!.Value.Season);
    }

    [Fact]
    public void BuildIsAllOrNothing()
    {
        // A partial layout is worse than none: a missing episode would look
        // like a gap and silently poison the offset for every later season.
        var mixed = new[]
        {
            @"D:\A\Show - S01E01 - a.mkv",
            @"D:\A\Show - 002 - b.mkv",
            @"D:\A\Show - S01E03 - c.mkv",
        };
        Assert.Null(EpisodePathLayout.Build(mixed));
    }

    [Fact]
    public void BuildReturnsLayoutWhenEveryPathParses()
    {
        var paths = new[]
        {
            @"D:\A\Show - S01E01 - a.mkv",
            @"D:\A\Show - S01E02 - b.mkv",
            @"D:\A\Show - S02E01 - c.mkv",
        };
        var layout = EpisodePathLayout.Build(paths);
        Assert.NotNull(layout);
        Assert.Equal(3, layout!.Count);
        Assert.Contains((1, 1), layout);
        Assert.Contains((2, 1), layout);
    }

    [Fact]
    public void BuildOnEmptyInputReturnsNull()
        => Assert.Null(EpisodePathLayout.Build(new string[0]));

    // The whole point: the half-merged state that blocks LocalOrderResolver
    // resolves correctly once the layout comes from paths.
    [Fact]
    public void HalfMergedLibraryResolvesFromPathLayout()
    {
        // Jellyfin's CURRENT view: S1 polluted with absolute numbers, S2 gone.
        var current = new List<(int Season, int Episode)>
        {
            (1, 1), (1, 2), (1, 21), (1, 22),   // S2 merged in as absolutes
            (3, 1), (3, 2),
        };
        // L3 cannot cope: S1 Count=4 but max=22, and season 2 is missing.
        Assert.Null(LocalOrderResolver.Compute(current, targetSeason: 3, targetEpisode: 1));

        // The paths still describe the original layout.
        var paths = new[]
        {
            @"D:\A\S - S01E01 - a.mkv", @"D:\A\S - S01E02 - b.mkv",
            @"D:\A\S - S02E01 - c.mkv", @"D:\A\S - S02E02 - d.mkv",
            @"D:\A\S - S03E01 - e.mkv", @"D:\A\S - S03E02 - f.mkv",
        };
        var layout = EpisodePathLayout.Build(paths);
        Assert.NotNull(layout);
        // S1 has 2, S2 has 2, so S3E1 is absolute 5.
        Assert.Equal(5, LocalOrderResolver.Compute(layout!, targetSeason: 3, targetEpisode: 1));
        Assert.Equal(6, LocalOrderResolver.Compute(layout!, targetSeason: 3, targetEpisode: 2));
    }

    [Fact]
    public void PathLayoutStillRefusesWhenTheLayoutItselfHasAGap()
    {
        // Rebuilding from paths must not become a licence to guess. A genuine
        // hole (an episode that has not aired) still returns null.
        var paths = new[]
        {
            @"D:\A\S - S01E01 - a.mkv", @"D:\A\S - S01E03 - c.mkv",  // E02 absent
            @"D:\A\S - S02E01 - d.mkv",
        };
        var layout = EpisodePathLayout.Build(paths);
        Assert.NotNull(layout);
        Assert.Null(LocalOrderResolver.Compute(layout!, targetSeason: 2, targetEpisode: 1));
    }
}
