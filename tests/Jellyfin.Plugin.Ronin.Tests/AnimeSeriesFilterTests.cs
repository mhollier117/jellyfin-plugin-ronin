// Design doc 2026-08-01, D3.1 / test U4: series selection is a pure function
// of (genres, tags, ancestorIds, config). Library scope gates BEFORE the
// genre/tag identification, over the four AnimeIdentificationModes.
// Regression pin: a series carrying the "Anime" tag but living outside the
// scoped library (the Alice in Borderland shape — a live-action D:\TV series
// merged via a stray tag on 2026-08-01) is excluded.
using Jellyfin.Plugin.Ronin.Configuration;
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class AnimeSeriesFilterTests
{
    private static readonly Guid AnimeLib = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid TvLib = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static readonly Guid[] InScope = [Guid.NewGuid(), AnimeLib];
    private static readonly Guid[] OutOfScope = [Guid.NewGuid(), TvLib];

    private static PluginConfiguration Config(AnimeIdentificationMode mode) => new()
    {
        AnimeIdentificationMode = mode,
        AnimeTargetTag = "Anime",
        LibraryIds = [AnimeLib.ToString("N")],
    };

    [Fact]
    public void Filter_InScope_GenreMode_Matches()
        => Assert.True(AnimeSeriesFilter.Matches(["Anime"], [], InScope, Config(AnimeIdentificationMode.Genre)));

    // Doc U4 core — RED against 1.0.4.0 (no scoping at all).
    [Theory]
    [InlineData(AnimeIdentificationMode.Genre)]
    [InlineData(AnimeIdentificationMode.Tag)]
    [InlineData(AnimeIdentificationMode.GenreOrTag)]
    [InlineData(AnimeIdentificationMode.GenreAndTag)]
    public void Filter_OutOfScope_Excluded_EvenWhenGenreAndTagMatch(AnimeIdentificationMode mode)
        => Assert.False(AnimeSeriesFilter.Matches(["Anime"], ["Anime"], OutOfScope, Config(mode)));

    // Empty LibraryIds = process nothing (fail-safe default) — RED today.
    [Fact]
    public void Filter_EmptyLibraryConfig_ExcludesEverything()
    {
        var config = Config(AnimeIdentificationMode.Genre);
        config.LibraryIds = [];
        Assert.False(AnimeSeriesFilter.Matches(["Anime"], ["Anime"], InScope, config));
    }

    // The Alice in Borderland regression pin — RED today.
    [Fact]
    public void Filter_TaggedSeriesOutsideScopedLibrary_Excluded()
        => Assert.False(AnimeSeriesFilter.Matches(
            ["Drama", "Thriller"], ["Anime"], OutOfScope, Config(AnimeIdentificationMode.Tag)));

    [Theory]
    [InlineData(AnimeIdentificationMode.Genre, true, false, true)]
    [InlineData(AnimeIdentificationMode.Genre, false, true, false)]
    [InlineData(AnimeIdentificationMode.Tag, true, false, false)]
    [InlineData(AnimeIdentificationMode.Tag, false, true, true)]
    [InlineData(AnimeIdentificationMode.GenreOrTag, true, false, true)]
    [InlineData(AnimeIdentificationMode.GenreOrTag, false, true, true)]
    [InlineData(AnimeIdentificationMode.GenreOrTag, false, false, false)]
    [InlineData(AnimeIdentificationMode.GenreAndTag, true, false, false)]
    [InlineData(AnimeIdentificationMode.GenreAndTag, false, true, false)]
    [InlineData(AnimeIdentificationMode.GenreAndTag, true, true, true)]
    public void Filter_ModeMatrix_InScope(AnimeIdentificationMode mode, bool hasGenre, bool hasTag, bool expected)
    {
        string[] genres = hasGenre ? ["Anime"] : ["Action"];
        string[] tags = hasTag ? ["Anime"] : ["Subbed"];
        Assert.Equal(expected, AnimeSeriesFilter.Matches(genres, tags, InScope, Config(mode)));
    }

    [Fact]
    public void Filter_CustomTag_Honored()
    {
        var config = Config(AnimeIdentificationMode.Tag);
        config.AnimeTargetTag = "MyAnime";
        Assert.True(AnimeSeriesFilter.Matches([], ["MyAnime"], InScope, config));
        Assert.False(AnimeSeriesFilter.Matches([], ["Anime"], InScope, config));
    }

    [Fact]
    public void Filter_NullGenresAndTags_NoMatch()
        => Assert.False(AnimeSeriesFilter.Matches(null, null, InScope, Config(AnimeIdentificationMode.GenreOrTag)));
}
