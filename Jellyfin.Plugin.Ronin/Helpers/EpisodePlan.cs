using System;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Outcome of a per-episode planning decision (merge or split).
/// </summary>
public enum PlanOutcome
{
    /// <summary>Episode already converged; write nothing.</summary>
    NoOp,

    /// <summary>Apply the write set carried by the plan.</summary>
    Update,

    /// <summary>Skip this episode entirely (no partial write).</summary>
    SkipEpisode,

    /// <summary>Skip the whole series (e.g. no Season 1 item to re-home into).</summary>
    SkipSeries
}

/// <summary>
/// Pure, unit-testable write set for one episode. Mirrors the server's own
/// re-home recipe (SeriesMetadataService): ParentIndexNumber + SeasonId +
/// SeasonName — never ParentId.
/// </summary>
/// <param name="Outcome">What the caller should do.</param>
/// <param name="ParentIndexNumber">New season number to write, when not null.</param>
/// <param name="SeasonId">New SeasonId to write, when not null.</param>
/// <param name="SeasonName">New SeasonName to write, when not null.</param>
/// <param name="IndexNumber">New episode number to write, when not null.</param>
public sealed record EpisodePlan(
    PlanOutcome Outcome,
    int? ParentIndexNumber = null,
    Guid? SeasonId = null,
    string? SeasonName = null,
    int? IndexNumber = null)
{
    /// <summary>A shared no-op plan.</summary>
    public static readonly EpisodePlan NoOpPlan = new(PlanOutcome.NoOp);

    /// <summary>A shared skip-episode plan.</summary>
    public static readonly EpisodePlan SkipEpisodePlan = new(PlanOutcome.SkipEpisode);

    /// <summary>A shared skip-series plan.</summary>
    public static readonly EpisodePlan SkipSeriesPlan = new(PlanOutcome.SkipSeries);
}
