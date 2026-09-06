// 2026-09-06: the merge left Mushoku Tensei permanently half-merged. Episodes
// resolved earlier by a remote lookup carried TVDB's absolute numbers, which
// count episodes the user does NOT own; the local-order fallback counts only
// files present, so it computed numbers two lower and every straggler landed on
// an occupied slot:
//
//   abs 48 held by S02E23 (remote)  <- local order computed 48 for S03E01
//   abs 49 held by S02E24 (remote)  <- local order computed 49 for S03E02
//   abs 55 held by S03E06 (remote)  <- local order computed 55 for S03E08
//
// The collision guard correctly refused, but the series can never converge
// while two numbering schemes coexist. The API returns the complete aired
// order including episodes not owned, so every episode resolves from one
// scheme regardless of what is on disk.
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class TvdbAbsoluteMapTests
{
    private const string Payload = @"{
      ""status"": ""success"",
      ""data"": {
        ""series"": { ""id"": 371310 },
        ""episodes"": [
          { ""id"": 1, ""seasonNumber"": 1, ""number"": 1,  ""absoluteNumber"": 1 },
          { ""id"": 2, ""seasonNumber"": 1, ""number"": 23, ""absoluteNumber"": 23 },
          { ""id"": 3, ""seasonNumber"": 2, ""number"": 23, ""absoluteNumber"": 48 },
          { ""id"": 4, ""seasonNumber"": 2, ""number"": 24, ""absoluteNumber"": 49 },
          { ""id"": 5, ""seasonNumber"": 3, ""number"": 1,  ""absoluteNumber"": 50 },
          { ""id"": 6, ""seasonNumber"": 0, ""number"": 1,  ""absoluteNumber"": 0 }
        ]
      }
    }";

    [Fact]
    public void MapsSeasonEpisodeToAbsolute()
    {
        var map = TvdbAbsoluteMap.Parse(Payload);
        Assert.Equal(1, map[(1, 1)]);
        Assert.Equal(23, map[(1, 23)]);
        Assert.Equal(50, map[(3, 1)]);
    }

    [Fact]
    public void CarriesTheOffsetTheLocalOrderCannotSee()
    {
        // The whole point: S2E23 is absolute 48, not 46. The local order would
        // say 46 because it counts only the 23 season-1 files actually held.
        var map = TvdbAbsoluteMap.Parse(Payload);
        Assert.Equal(48, map[(2, 23)]);
        Assert.Equal(49, map[(2, 24)]);
    }

    [Fact]
    public void SpecialsAreExcluded()
    {
        // Season 0 has no position in the aired order and must never be
        // renumbered - the merge leaves specials alone by design.
        var map = TvdbAbsoluteMap.Parse(Payload);
        Assert.False(map.ContainsKey((0, 1)));
    }

    [Fact]
    public void AcceptsDataAsABareArray()
    {
        var map = TvdbAbsoluteMap.Parse(
            @"{""data"":[{""seasonNumber"":4,""number"":2,""absoluteNumber"":77}]}");
        Assert.Equal(77, map[(4, 2)]);
    }

    [Fact]
    public void AcceptsNumericStrings()
    {
        var map = TvdbAbsoluteMap.Parse(
            @"{""data"":{""episodes"":[{""seasonNumber"":""2"",""number"":""5"",""absoluteNumber"":""30""}]}}");
        Assert.Equal(30, map[(2, 5)]);
    }

    [Fact]
    public void FirstWriterWinsSoPayloadOrderCannotChangeTheResult()
    {
        // The endpoint can repeat an episode across alternate orderings.
        var map = TvdbAbsoluteMap.Parse(
            @"{""data"":{""episodes"":[
                {""seasonNumber"":1,""number"":1,""absoluteNumber"":1},
                {""seasonNumber"":1,""number"":1,""absoluteNumber"":999}]}}");
        Assert.Equal(1, map[(1, 1)]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData(@"{""status"":""failure""}")]
    [InlineData(@"{""data"":{}}")]
    [InlineData(@"{""data"":{""episodes"":""nope""}}")]
    public void MalformedPayloadsYieldAnEmptyMapRatherThanThrowing(string? json)
    {
        // A bad body must fall through to the next resolver, never take the
        // whole merge task down.
        Assert.Empty(TvdbAbsoluteMap.Parse(json));
    }

    [Fact]
    public void EpisodesMissingAnAbsoluteNumberAreSkipped()
    {
        // An unaired or unnumbered episode has no absolute position. Guessing
        // one would renumber a real episode onto the wrong slot.
        var map = TvdbAbsoluteMap.Parse(
            @"{""data"":{""episodes"":[
                {""seasonNumber"":1,""number"":1},
                {""seasonNumber"":1,""number"":2,""absoluteNumber"":null},
                {""seasonNumber"":1,""number"":3,""absoluteNumber"":0},
                {""seasonNumber"":1,""number"":4,""absoluteNumber"":4}]}}");
        Assert.Single(map);
        Assert.Equal(4, map[(1, 4)]);
    }
}
