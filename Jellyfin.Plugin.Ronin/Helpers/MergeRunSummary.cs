using System.Globalization;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Tally for one merge run, and the verdict on whether it actually achieved
/// anything.
/// <para>
/// Motivated by the 2026-09-05 incident: the task logged "Finished merging all
/// anime seasons into season 1.", reported <c>Completed</c>, and drove the
/// progress bar to 100% while skipping 474 of 474 episodes of one series.
/// Nothing in the task list distinguished that from a healthy run, so the
/// underlying regression survived undetected across many runs.
/// </para>
/// <para>
/// The verdict is deliberately SERIES-level, not run-level. A first attempt at
/// "no merges plus any skip is a failure" fired immediately on a healthy,
/// fully-converged library: nothing left to merge, and 3 episodes skipped
/// because their predecessor has not aired. Those skips are correct and
/// permanent, and failing the task forever over them is crying wolf. What
/// actually indicates breakage is a series that had real work and resolved
/// NONE of it.
/// </para>
/// </summary>
/// <param name="SeriesTouched">Number of series examined.</param>
/// <param name="Merged">Episodes actually re-homed into season 1.</param>
/// <param name="Skipped">Episodes skipped because their absolute number could not be resolved.</param>
/// <param name="SeriesResolvedNothing">Series that had substantial work and merged none of it.</param>
public sealed record MergeRunSummary(
    int SeriesTouched,
    int Merged,
    int Skipped,
    int SeriesResolvedNothing)
{
    /// <summary>
    /// Minimum skipped episodes before a zero-merge series counts as broken.
    /// <para>
    /// A currently-airing season legitimately leaves a small tail unresolved
    /// (an episode whose predecessor has not aired yet), and several such
    /// series in one library can add up. The incident this guards against was
    /// 474 skips in a single series, so the two are separated by two orders of
    /// magnitude - the threshold only has to avoid the benign tail.
    /// </para>
    /// </summary>
    public const int SeriesFailureSkipThreshold = 10;

    private int SafeMerged => Merged > 0 ? Merged : 0;

    private int SafeSkipped => Skipped > 0 ? Skipped : 0;

    /// <summary>
    /// Gets a value indicating whether at least one series had substantial
    /// work and resolved none of it.
    /// </summary>
    public bool IsTotalFailure => SeriesResolvedNothing > 0;

    /// <summary>
    /// Gets a value indicating whether the run made progress but left some
    /// episodes unresolved.
    /// </summary>
    public bool IsPartial => SafeMerged > 0 && SafeSkipped > 0;

    /// <summary>
    /// Decides whether one series' outcome indicates breakage rather than a
    /// benign unaired-episode tail.
    /// </summary>
    /// <param name="seriesMerged">Episodes merged for this series.</param>
    /// <param name="seriesSkipped">Episodes skipped for this series.</param>
    /// <returns>True when the series had real work and resolved none of it.</returns>
    public static bool SeriesResolvedNothingOutright(int seriesMerged, int seriesSkipped)
        => seriesMerged <= 0 && seriesSkipped >= SeriesFailureSkipThreshold;

    /// <summary>
    /// Builds a one-line, self-explanatory summary for the log.
    /// </summary>
    /// <returns>The summary text.</returns>
    public string Describe() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} series examined, {1} episodes merged, {2} skipped (absolute number unresolved), {3} series resolved nothing",
        SeriesTouched > 0 ? SeriesTouched : 0,
        SafeMerged,
        SafeSkipped,
        SeriesResolvedNothing > 0 ? SeriesResolvedNothing : 0);
}
