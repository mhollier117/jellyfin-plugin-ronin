// Design doc 2026-08-01, D2.4 / test U15: SeasonCleanup.ShouldDeleteSeason is
// retained as a tested tripwire, but Ronin no longer deletes seasons at all —
// the doc proves the 1.0.4.0 guard unsound (LibraryManager.DeleteItem expands
// through the AncestorIds table, and Episode.GetAncestorIds injects SeasonId,
// so "0 children by ParentId" can still cascade-delete episodes). The
// contract is extended: a season backed by a folder on disk is NEVER
// deletable, no matter what the child count says.
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class SeasonCleanupTripwireTests
{
    // Doc U15 — RED against the 1.0.4.0 guard (child count 0 was enough).
    [Fact]
    public void SeasonCleanup_PhysicalSeason_NeverDeletable()
    {
        Assert.False(SeasonCleanup.ShouldDeleteSeason(2, 0, seasonHasPhysicalPath: true));
        Assert.False(SeasonCleanup.ShouldDeleteSeason(5, 0, seasonHasPhysicalPath: true));
    }

    [Fact]
    public void SeasonCleanup_VirtualSeason_ContractUnchanged()
    {
        Assert.True(SeasonCleanup.ShouldDeleteSeason(2, 0, seasonHasPhysicalPath: false));
        Assert.False(SeasonCleanup.ShouldDeleteSeason(2, 3, seasonHasPhysicalPath: false));
    }
}
