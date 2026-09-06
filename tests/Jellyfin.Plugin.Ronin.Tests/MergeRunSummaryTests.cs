// 2026-09-05: the merge task logged "Finished merging all anime seasons into
// season 1." and reported Completed while skipping 474 of 474 episodes of one
// series. LastExecutionResult said Completed, progress reached 100%, and the
// library was untouched - so the regression that caused it (see
// EpisodePathLayout) survived undetected across many runs.
//
// A run that had real work and achieved nothing must FAIL, so Jellyfin
// surfaces it in the task list instead of showing a green Completed.
//
// The verdict is SERIES-level on purpose. A first attempt at "no merges plus
// any skip is a failure" fired immediately on a healthy, fully-converged
// library: nothing left to merge, 3 episodes skipped because their predecessor
// had not aired. Those skips are correct and permanent; failing forever over
// them is crying wolf.
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class MergeRunSummaryTests
{
    [Fact]
    public void NothingToDo_IsNotAFailure()
    {
        var s = new MergeRunSummary(SeriesTouched: 5, Merged: 0, Skipped: 0,
            SeriesResolvedNothing: 0);
        Assert.False(s.IsTotalFailure);
        Assert.False(s.IsPartial);
    }

    [Fact]
    public void ConvergedLibraryWithUnairedTail_IsNotAFailure()
    {
        // The exact false positive that the first implementation produced:
        // fully merged, nothing left to do, a few permanently-unresolvable
        // episodes whose predecessor has not aired.
        var s = new MergeRunSummary(SeriesTouched: 18, Merged: 0, Skipped: 3,
            SeriesResolvedNothing: 0);
        Assert.False(s.IsTotalFailure);
    }

    [Fact]
    public void SeriesThatResolvedNothing_FailsTheRun()
    {
        // The 2026-09-05 shape: Bleach skipped 474 of 474.
        var s = new MergeRunSummary(SeriesTouched: 18, Merged: 0, Skipped: 474,
            SeriesResolvedNothing: 1);
        Assert.True(s.IsTotalFailure);
    }

    [Fact]
    public void OneBrokenSeriesFailsEvenWhenOthersSucceed()
    {
        // Otherwise one broken series hides behind seventeen healthy ones.
        var s = new MergeRunSummary(SeriesTouched: 18, Merged: 239, Skipped: 474,
            SeriesResolvedNothing: 1);
        Assert.True(s.IsTotalFailure);
    }

    [Fact]
    public void CleanRun_IsNeitherFailureNorPartial()
    {
        var s = new MergeRunSummary(18, 239, 0, 0);
        Assert.False(s.IsTotalFailure);
        Assert.False(s.IsPartial);
    }

    [Fact]
    public void SomeProgressWithASmallTail_IsPartialNotFailure()
    {
        var s = new MergeRunSummary(18, 200, 1, 0);
        Assert.False(s.IsTotalFailure);
        Assert.True(s.IsPartial);
    }

    [Theory]
    // Benign: an airing season's tail. Must NOT flag the series.
    [InlineData(0, 1, false)]
    [InlineData(0, 3, false)]
    [InlineData(0, 9, false)]
    // Substantial work resolved to nothing: broken.
    [InlineData(0, 10, true)]
    [InlineData(0, 474, true)]
    // Any progress at all means the resolver is working.
    [InlineData(1, 474, false)]
    [InlineData(239, 20, false)]
    public void SeriesVerdictSeparatesBreakageFromAnUnairedTail(
        int merged, int skipped, bool expected)
        => Assert.Equal(expected,
            MergeRunSummary.SeriesResolvedNothingOutright(merged, skipped));

    [Fact]
    public void ThresholdSitsFarBelowTheIncidentAndAboveTheBenignTail()
    {
        Assert.True(MergeRunSummary.SeriesFailureSkipThreshold > 3,
            "must not fire on a converged library's unaired tail");
        Assert.True(MergeRunSummary.SeriesFailureSkipThreshold < 474,
            "must fire on the 2026-09-05 incident");
    }

    [Fact]
    public void DescribeCarriesTheCountsSoTheLogIsSelfExplanatory()
    {
        var text = new MergeRunSummary(18, 239, 3, 1).Describe();
        Assert.Contains("18", text, System.StringComparison.Ordinal);
        Assert.Contains("239", text, System.StringComparison.Ordinal);
        Assert.Contains("3", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeCountsAreClampedRatherThanThrowing()
    {
        var s = new MergeRunSummary(-1, -5, -2, -1);
        Assert.False(s.IsTotalFailure);
        Assert.NotNull(s.Describe());
    }
}
