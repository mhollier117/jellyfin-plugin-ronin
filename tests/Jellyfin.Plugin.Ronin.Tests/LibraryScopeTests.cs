// Design doc 2026-08-01, D3.1/D3.2 / tests U1-U3: library scoping is a set
// intersection of the configured library ids with the item's ancestor ids.
// Empty configuration = process NOTHING (fail-safe: every Ronin task is
// destructive re-organization or bulk mutation, and the incident that
// motivates this fix was a scope-everything failure). Ids are parsed
// tolerantly ("N" and dashed formats); malformed entries are ignored.
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class LibraryScopeTests
{
    private static readonly Guid Lib1 = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid Lib2 = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly Guid SomeParent = Guid.Parse("12121212-3434-5656-7878-909090909090");

    // Doc U1 — RED against 1.0.4.0 (no scoping existed).
    [Fact]
    public void LibraryScope_EmptyConfig_ProcessesNothing()
    {
        Assert.False(LibraryScope.IsInScope([], [Lib1, SomeParent]));
        Assert.False(LibraryScope.IsInScope(null, [Lib1, SomeParent]));
    }

    // Doc U2.
    [Fact]
    public void LibraryScope_MatchingAncestor_True()
        => Assert.True(LibraryScope.IsInScope([Lib1.ToString("N")], [SomeParent, Lib1]));

    [Fact]
    public void LibraryScope_NonMatchingAncestor_False()
        => Assert.False(LibraryScope.IsInScope([Lib1.ToString("N")], [SomeParent, Lib2]));

    // Doc U3 — VirtualFolderInfo.ItemId is "N" format; be tolerant of both.
    [Fact]
    public void LibraryScope_ParsesNFormat_And_DashedFormat()
    {
        Assert.True(LibraryScope.IsInScope([Lib1.ToString("N")], [Lib1]));
        Assert.True(LibraryScope.IsInScope([Lib1.ToString("D")], [Lib1]));
        Assert.True(LibraryScope.IsInScope([Lib1.ToString("N").ToUpperInvariant()], [Lib1]));
    }

    [Fact]
    public void LibraryScope_MalformedEntries_Ignored()
    {
        Assert.False(LibraryScope.IsInScope(["not-a-guid", "12345"], [Lib1]));
        Assert.True(LibraryScope.IsInScope(["not-a-guid", Lib1.ToString("N")], [Lib1]));
    }

    [Fact]
    public void LibraryScope_MultiLibrary()
    {
        string[] both = [Lib1.ToString("N"), Lib2.ToString("N")];
        Assert.True(LibraryScope.IsInScope(both, [SomeParent, Lib2]));
        Assert.True(LibraryScope.IsInScope(both, [Lib1]));
        Assert.False(LibraryScope.IsInScope(both, [SomeParent]));
    }
}
