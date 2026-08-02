namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Per-run circuit breaker for a scrape source. Once tripped (e.g. on the
/// first HTTP 403 from AniDB), all further lookups through it are skipped for
/// the remainder of the task run (design doc D2.3).
/// </summary>
public sealed class ScrapeCircuitBreaker
{
    /// <summary>
    /// Gets a value indicating whether the breaker is open (source disabled
    /// for the rest of the run).
    /// </summary>
    public bool IsOpen { get; private set; }

    /// <summary>
    /// Opens the breaker; no further requests should be sent through it.
    /// </summary>
    public void Trip() => IsOpen = true;
}
