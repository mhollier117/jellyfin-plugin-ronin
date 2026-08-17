// 2026-08-17: the split task requires per-episode Tvdb ids, which AniDB-only
// libraries lack - and size-based local inversion is poisoned by the merged
// season-1 blob (contiguous 1..N makes the "aired S1 size" unknowable). The
// escape: VIRTUAL rows for merged-away seasons carry (airedS, airedE,
// premiereDate), and real blob rows carry (absolute, premiereDate) - a date
// JOIN recovers aired identity with no sizes, no ids, no network.
//   - D1: unique dates join directly
//   - D2: a real row matching no virtual is aired season 1: aired episode ==
//         its absolute number
//   - D3: duplicate-date groups pair in sorted order (abs asc <-> aired asc)
//   - D4: a duplicate-date group with mismatched counts resolves nobody
//   - D5: rows without dates resolve nobody (never guess)
//   - D6: virtual rows for season 0 are ignored (specials are not split)
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class AiredIdentityMapTests
{
    private static DateTime D(int day) => new(2024, 1, day);

    // D1 + D2: two-season show, S1 has 2 eps (no virtuals), S2 merged away
    // (virtuals exist).
    [Fact]
    public void UniqueDates_JoinAndS1Fallback()
    {
        var real = new List<RealEpisodeRef>
        {
            new(1, D(1)), new(2, D(8)),           // aired S1 (unmatched)
            new(3, D(15)), new(4, D(22)),         // merged S2 content
        };
        var virt = new List<VirtualEpisodeRef>
        {
            new(2, 1, D(15)), new(2, 2, D(22)),
        };
        var map = AiredIdentityMap.Build(real, virt);

        Assert.Equal((2, 1), map[3]);
        Assert.Equal((2, 2), map[4]);
        Assert.Equal((1, 1), map[1]);
        Assert.Equal((1, 2), map[2]);
    }

    // D3: double-airing day - two episodes share a premiere date.
    [Fact]
    public void DuplicateDates_PairInSortedOrder()
    {
        var real = new List<RealEpisodeRef> { new(5, D(5)), new(6, D(5)) };
        var virt = new List<VirtualEpisodeRef>
        {
            new(2, 3, D(5)), new(2, 4, D(5)),
        };
        var map = AiredIdentityMap.Build(real, virt);

        Assert.Equal((2, 3), map[5]);
        Assert.Equal((2, 4), map[6]);
    }

    // D4: count mismatch inside a date group -> nobody in that group resolves.
    [Fact]
    public void DuplicateDates_CountMismatch_ResolvesNobody()
    {
        var real = new List<RealEpisodeRef> { new(5, D(5)), new(6, D(5)) };
        var virt = new List<VirtualEpisodeRef> { new(2, 3, D(5)) };
        var map = AiredIdentityMap.Build(real, virt);

        Assert.False(map.ContainsKey(5));
        Assert.False(map.ContainsKey(6));
    }

    // D5: no date -> not resolved, not guessed.
    [Fact]
    public void MissingDates_ResolveNobody()
    {
        var real = new List<RealEpisodeRef> { new(7, null) };
        var virt = new List<VirtualEpisodeRef> { new(2, 5, D(9)) };
        var map = AiredIdentityMap.Build(real, virt);

        Assert.False(map.ContainsKey(7));
    }

    // D7: chronology guard - absolutes are chronological, so a claimed
    // "aired S1" row with a HIGHER absolute than a mapped S2+ row is a
    // misclassification (its virtual twin lost its date) -> dropped.
    [Fact]
    public void S1FallbackBeyondMappedRows_Dropped()
    {
        var real = new List<RealEpisodeRef>
        {
            new(1, D(1)),                       // true S1
            new(3, D(15)),                      // S2E1, joined by date
            new(4, D(22)),                      // S2E2 whose virtual lost its
                                                // date -> matches nothing
        };
        var virt = new List<VirtualEpisodeRef>
        {
            new(2, 1, D(15)), new(2, 2, null),
        };
        var map = AiredIdentityMap.Build(real, virt);

        Assert.Equal((1, 1), map[1]);
        Assert.Equal((2, 1), map[3]);
        Assert.False(map.ContainsKey(4));       // NOT claimed as S1E4
    }

    // D6: specials virtuals are ignored even on matching dates.
    [Fact]
    public void SpecialVirtuals_Ignored()
    {
        var real = new List<RealEpisodeRef> { new(8, D(11)) };
        var virt = new List<VirtualEpisodeRef> { new(0, 2, D(11)) };
        var map = AiredIdentityMap.Build(real, virt);

        Assert.Equal((1, 8), map[8]);      // falls back to aired-S1 identity
    }
}
