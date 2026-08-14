// 2026-08-14 incident: 29 episodes skipped "absolute episode number
// unresolved" (Solo Leveling S2: no Tvdb episode ids at all; TenSura: TVDB
// scrape failures). Worse, Solo Leveling's S2 episodes carry S1's AniDB ids
// (AniDB models sequels as separate series), so a SUCCESSFUL AniDB lookup
// would have returned S1's absolute numbers and renumbered S2E1 onto S1E1.
// Remote lookups are therefore fallible in both directions - this resolver
// computes the absolute number from the episodes already in hand, and only
// when the local library is provably gapless:
//   - L1: every season before the target is present and contiguous from 1
//   - L2: the target's season is contiguous from 1 through the target
//   - L3: any gap -> null (a guess would renumber onto the wrong slot)
//   - L4: season 1 episodes are their own absolute number
//   - L5: gaps AFTER the target episode do not matter
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class LocalOrderResolverTests
{
    private static List<(int Season, int Episode)> Eps(params (int, int)[] pairs)
        => pairs.ToList();

    // L4
    [Fact]
    public void SeasonOne_IsItsOwnAbsoluteNumber()
    {
        var eps = Eps((1, 1), (1, 2), (1, 3));
        Assert.Equal(2, LocalOrderResolver.Compute(eps, targetSeason: 1, targetEpisode: 2));
    }

    // The Solo Leveling case: S1 has 12 contiguous, S2E1 must be 13.
    [Fact]
    public void GaplessPriorSeason_CumulativePosition()
    {
        var eps = Eps(
            (1, 1), (1, 2), (1, 3), (1, 4), (1, 5), (1, 6),
            (1, 7), (1, 8), (1, 9), (1, 10), (1, 11), (1, 12),
            (2, 1), (2, 2), (2, 3));
        Assert.Equal(13, LocalOrderResolver.Compute(eps, targetSeason: 2, targetEpisode: 1));
        Assert.Equal(15, LocalOrderResolver.Compute(eps, targetSeason: 2, targetEpisode: 3));
    }

    // L3: hole in a prior season -> cannot know the true offset.
    [Fact]
    public void GapInPriorSeason_ReturnsNull()
    {
        var eps = Eps((1, 1), (1, 3), (2, 1));   // S1E2 missing
        Assert.Null(LocalOrderResolver.Compute(eps, targetSeason: 2, targetEpisode: 1));
    }

    // L3: prior season entirely absent.
    [Fact]
    public void MissingPriorSeason_ReturnsNull()
    {
        var eps = Eps((1, 1), (1, 2), (3, 1));   // no season 2 at all
        Assert.Null(LocalOrderResolver.Compute(eps, targetSeason: 3, targetEpisode: 1));
    }

    // L2: hole in the target season before the target episode.
    [Fact]
    public void GapBeforeTargetInOwnSeason_ReturnsNull()
    {
        var eps = Eps((1, 1), (1, 2), (2, 1), (2, 3));   // S2E2 missing
        Assert.Null(LocalOrderResolver.Compute(eps, targetSeason: 2, targetEpisode: 3));
    }

    // L5: a gap after the target is irrelevant.
    [Fact]
    public void GapAfterTarget_StillResolves()
    {
        var eps = Eps((1, 1), (1, 2), (2, 1), (2, 2), (2, 4));   // S2E3 missing
        Assert.Equal(4, LocalOrderResolver.Compute(eps, targetSeason: 2, targetEpisode: 2));
    }

    // Specials never count toward the ordinal.
    [Fact]
    public void SpecialsExcludedFromOrdinal()
    {
        var eps = Eps((0, 1), (1, 1), (1, 2), (2, 1));
        Assert.Equal(3, LocalOrderResolver.Compute(eps, targetSeason: 2, targetEpisode: 1));
    }
}
