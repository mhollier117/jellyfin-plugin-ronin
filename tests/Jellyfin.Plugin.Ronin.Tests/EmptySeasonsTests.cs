// Merged-look presentation: after the self-healing merge, episodes are
// re-homed to Season 1 via SeasonId, but physical "Season NN" folders (and
// stale non-virtual season rows) keep their season items alive - scans
// re-create them from disk, and deleting them was the 1.0.4.0 data-loss bug.
// The UI fix is to HIDE display-empty seasons client-side. Display-empty is
// defined by SeasonId references only: an episode whose file still lives in
// the Season 2 folder (ParentId) but is re-homed to Season 1 (SeasonId) must
// NOT keep Season 2 visible.
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public sealed class EmptySeasonsTests
{
    private static readonly Guid S1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid S2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid S3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid S0 = Guid.Parse("00000000-aaaa-aaaa-aaaa-000000000000");

    [Fact]
    public void SeasonReferencedBySeasonIdIsKept()
    {
        var hidden = EmptySeasons.Compute(
            [(S1, 1), (S2, 2)],
            [S1, S2]);

        Assert.Empty(hidden);
    }

    [Fact]
    public void UnreferencedSeasonAboveOneIsHidden()
    {
        // The incident shape: files still in the Season 2/3 folders, but every
        // episode's SeasonId re-homed to Season 1.
        var hidden = EmptySeasons.Compute(
            [(S1, 1), (S2, 2), (S3, 3)],
            [S1, S1, S1]);

        Assert.Equal([S2, S3], hidden);
    }

    [Fact]
    public void SeasonOneIsNeverHidden()
    {
        // Even a series whose episodes all sit in Specials keeps Season 1.
        var hidden = EmptySeasons.Compute(
            [(S1, 1), (S0, 0)],
            [S0]);

        Assert.Empty(hidden);
    }

    [Fact]
    public void SpecialsAreNeverHidden()
    {
        var hidden = EmptySeasons.Compute(
            [(S0, 0), (S1, 1), (S2, 2)],
            [S1]);

        Assert.Equal([S2], hidden);
    }

    [Fact]
    public void SeasonWithoutIndexNumberIsNeverHidden()
    {
        var hidden = EmptySeasons.Compute(
            [(S1, 1), (S2, null)],
            [S1]);

        Assert.Empty(hidden);
    }

    [Fact]
    public void PlaceholderReferenceKeepsSeasonVisible()
    {
        // A virtual (missing-episode placeholder) episode referencing the
        // season is content: hiding it would hide the placeholder view.
        var hidden = EmptySeasons.Compute(
            [(S1, 1), (S2, 2)],
            [S1, S2]); // second ref comes from a placeholder - same shape

        Assert.Empty(hidden);
    }

    [Fact]
    public void NoSeasonsMeansNothingHidden()
    {
        Assert.Empty(EmptySeasons.Compute([], [S1]));
    }
}
