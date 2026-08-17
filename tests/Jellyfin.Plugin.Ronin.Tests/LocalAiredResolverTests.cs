// The inverse of LocalOrderResolver: given an ABSOLUTE number, recover the
// aired (season, episode) from season sizes already in the library (real +
// virtual rows - virtuals are the server's cached provider structure).
// Needed because the split task's TVDB path requires per-episode Tvdb ids,
// which AniDB-only libraries (Solo Leveling, legacy Bleach) do not have -
// without this, a round-trip silently leaves those series merged.
//   - R1: absolute within season 1 -> (1, abs)
//   - R2: cumulative walk across gapless seasons
//   - R3: absolute beyond all known seasons -> null
//   - R4: a season whose contiguous prefix ends before its max present
//         index cannot be walked THROUGH (unknowable size) -> null for
//         anything beyond it; still resolvable before it
//   - R5: specials (season 0) never count
//   - R6: duplicate pairs (real + virtual overlap) are tolerated
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class LocalAiredResolverTests
{
    private static List<(int Season, int Episode)> Eps(params (int, int)[] p)
        => p.ToList();

    private static readonly List<(int, int)> TwoSeasons = Eps(
        (1, 1), (1, 2), (1, 3), (1, 4), (1, 5), (1, 6),
        (1, 7), (1, 8), (1, 9), (1, 10), (1, 11), (1, 12),
        (2, 1), (2, 2), (2, 3), (2, 4), (2, 5), (2, 6),
        (2, 7), (2, 8), (2, 9), (2, 10), (2, 11), (2, 12), (2, 13));

    // R1
    [Fact]
    public void WithinSeasonOne()
    {
        Assert.Equal((1, 5), LocalAiredResolver.Compute(TwoSeasons, 5));
        Assert.Equal((1, 12), LocalAiredResolver.Compute(TwoSeasons, 12));
    }

    // R2 - the Solo Leveling case in reverse: abs 13 -> S2E1, abs 25 -> S2E13
    [Fact]
    public void CumulativeWalk()
    {
        Assert.Equal((2, 1), LocalAiredResolver.Compute(TwoSeasons, 13));
        Assert.Equal((2, 13), LocalAiredResolver.Compute(TwoSeasons, 25));
    }

    // R3
    [Fact]
    public void BeyondKnownSeasons_Null()
    {
        Assert.Null(LocalAiredResolver.Compute(TwoSeasons, 26));
    }

    // R4 - S1 present as 1..3 and 5..8 (hole at 4): resolvable up to 3,
    // unknowable beyond.
    [Fact]
    public void GapBlocksWalkThrough()
    {
        var eps = Eps((1, 1), (1, 2), (1, 3), (1, 5), (1, 6), (1, 7), (1, 8),
                      (2, 1), (2, 2));
        Assert.Equal((1, 2), LocalAiredResolver.Compute(eps, 2));
        Assert.Null(LocalAiredResolver.Compute(eps, 5));
        Assert.Null(LocalAiredResolver.Compute(eps, 9));
    }

    // R5
    [Fact]
    public void SpecialsIgnored()
    {
        var eps = Eps((0, 1), (0, 2), (1, 1), (1, 2), (2, 1));
        Assert.Equal((2, 1), LocalAiredResolver.Compute(eps, 3));
    }

    // R6
    [Fact]
    public void DuplicatePairsTolerated()
    {
        var eps = Eps((1, 1), (1, 1), (1, 2), (1, 2), (2, 1), (2, 1));
        Assert.Equal((2, 1), LocalAiredResolver.Compute(eps, 3));
    }
}
