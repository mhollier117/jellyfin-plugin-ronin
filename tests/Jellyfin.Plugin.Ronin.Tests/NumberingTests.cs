// Design doc 2026-08-01, D2.3 / tests U10-U11: a series' numbering is
// "absolute" (no scraping, no renumbering needed) when episode numbers are
// distinct and strictly increasing when ordered by (season, episode) — gaps
// allowed, because missing episodes are normal in this library. The shipped
// 1.0.4.0 heuristic demanded a gapless 1..N run, forcing pointless (and, for
// AniDB, 403-doomed) scraping on libraries with missing episodes.
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class NumberingTests
{
    [Fact]
    public void Numbering_Gapless1toN_IsAbsolute()
        => Assert.True(Numbering.IsAbsoluteNumbering(
        [
            (1, 1), (1, 2), (2, 3), (2, 4)
        ]));

    // Doc U10 — RED against 1.0.4.0 (gapless 1..N required).
    [Fact]
    public void Numbering_DistinctIncreasingWithGaps_IsAbsolute_NoScrape()
        => Assert.True(Numbering.IsAbsoluteNumbering(
        [
            (1, 1), (1, 2), (1, 5), (2, 7), (2, 9)
        ]));

    [Fact]
    public void Numbering_StartsAboveOne_StillAbsolute()
        => Assert.True(Numbering.IsAbsoluteNumbering(
        [
            (1, 5), (1, 6), (2, 8)
        ]));

    // Doc U11.
    [Fact]
    public void Numbering_DuplicateOnes_RequiresRenumber()
        => Assert.False(Numbering.IsAbsoluteNumbering(
        [
            (1, 1), (1, 2), (2, 1), (2, 2)
        ]));

    [Fact]
    public void Numbering_DuplicateAnywhere_RequiresRenumber()
        => Assert.False(Numbering.IsAbsoluteNumbering(
        [
            (1, 2), (2, 2)
        ]));

    // Per-season numbering with offsets: distinct overall, but not increasing
    // across seasons — must renumber.
    [Fact]
    public void Numbering_PerSeasonOffsets_RequireRenumber()
        => Assert.False(Numbering.IsAbsoluteNumbering(
        [
            (1, 5), (1, 6), (2, 1), (2, 2)
        ]));

    [Fact]
    public void Numbering_Empty_NotAbsolute()
        => Assert.False(Numbering.IsAbsoluteNumbering([]));

    [Fact]
    public void Numbering_UnorderedInput_IsSortedBySeasonThenEpisode()
        => Assert.True(Numbering.IsAbsoluteNumbering(
        [
            (2, 3), (1, 1), (1, 2), (2, 4)
        ]));
}
