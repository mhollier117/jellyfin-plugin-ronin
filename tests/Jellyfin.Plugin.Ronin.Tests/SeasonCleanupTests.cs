// Edge-case inventory — reproduces the DATA LOSS observed 2026-08-01:
// MergeSeasonsTask deleted every season with IndexNumber > 1 unconditionally,
// commenting "We know it's empty because we just moved all episodes to Season 1".
// It was not empty. Jellyfin 12 cascades parent->child deletes, so each season
// took its remaining episodes with it: Naruto lost 185 of 220 library entries,
// Naruto Shippuden ~468 of 508, Dr. STONE 69 of 94.
//
// - season above 1 with NO children -> safe to delete
// - season above 1 WITH children  -> MUST NOT delete (the regression guard)
// - season 1 -> never deleted (it is the merge target)
// - season 0 / specials -> never deleted
// - unnumbered season -> never deleted (cannot reason about it)
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class SeasonCleanupTests
{
    [Fact]
    public void EmptySeasonAboveOne_IsDeletable()
        => Assert.True(SeasonCleanup.ShouldDeleteSeason(seasonIndexNumber: 2, childEpisodeCount: 0));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(220)]
    public void SeasonWithRemainingEpisodes_IsNeverDeleted(int children)
        => Assert.False(SeasonCleanup.ShouldDeleteSeason(seasonIndexNumber: 2, childEpisodeCount: children));

    [Fact]
    public void SeasonOne_IsNeverDeleted()
    {
        Assert.False(SeasonCleanup.ShouldDeleteSeason(1, 0));
        Assert.False(SeasonCleanup.ShouldDeleteSeason(1, 10));
    }

    [Fact]
    public void Specials_AreNeverDeleted()
        => Assert.False(SeasonCleanup.ShouldDeleteSeason(0, 0));

    [Fact]
    public void UnnumberedSeason_IsNeverDeleted()
        => Assert.False(SeasonCleanup.ShouldDeleteSeason(null, 0));
}
