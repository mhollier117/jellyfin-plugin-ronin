using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Ronin.Configuration;

/// <summary>
/// Plugin configuration for Ronin.
/// Contains settings used by the plugin tasks and frontend injection.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets whether canon/filler badges should appear on individual episode pages.
    /// </summary>
    public bool ShowBadgesOnEpisodePage { get; set; } = true;

    /// <summary>
    /// Gets or sets whether canon/filler badges should appear in season episode lists.
    /// </summary>
    public bool ShowBadgesOnSeasonList { get; set; } = true;

    /// <summary>
    /// Gets or sets whether badges should use colored styling (e.g., red filler, green canon).
    /// </summary>
    public bool EnableBadgeColors { get; set; } = true;

    /// <summary>
    /// Gets or sets minimum delay between external DB (TheTVDB/AniDB) API requests in milliseconds to comply with rate limits and avoid temporary blocks.
    /// </summary>
    public int DbRateLimitMs { get; set; } = 2000;

    /// <summary>
    /// Gets or sets the TheTVDB v4 API key used to resolve absolute episode numbers.
    /// <para>
    /// Without a key the resolver falls back to scraping thetvdb.com HTML, which is
    /// fragile, and to AniDB, which answers 403 Forbidden to scrapes. Both failing is
    /// what forces the local-order fallback, and the local order can only count
    /// episodes you actually hold - so a series with missing episodes gets numbers
    /// that disagree with any already resolved remotely, and the merge collides.
    /// </para>
    /// <para>
    /// The API answers with the complete aired order including episodes you do not
    /// own, which is the only source that stays correct with gaps. A free key is
    /// available at https://thetvdb.com/api-information.
    /// </para>
    /// </summary>
    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional TheTVDB subscriber PIN. Only required for
    /// user-supported ("v4 user") keys; leave empty for a project API key.
    /// </summary>
    public string TvdbSubscriberPin { get; set; } = string.Empty;

    /// <summary>
    /// Determines how anime series are identified (Genre, Tag, or combination).
    /// </summary>
    public AnimeIdentificationMode AnimeIdentificationMode { get; set; }
        = AnimeIdentificationMode.Genre;

    /// <summary>
    /// Tag name used when AnimeIdentificationMode includes Tag.
    /// </summary>
    public string AnimeTargetTag { get; set; } = "Anime";

    /// <summary>
    /// Gets or sets a value indicating whether to trigger a non-destructive metadata refresh for each series after its episodes have been re-indexed. 
    /// This updates the UI episode counts and season structure immediately.
    /// </summary>
    public bool RefreshSeriesAfterProcessed { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to rename the season folder when all episodes are merged into a single season.
    /// </summary>
    public bool RenameWhenSingleSeason { get; set; } = true;

    /// <summary>
    /// Gets or sets the name to assign to the single season when merging all episodes into one season.
    /// </summary>
    public string SingleSeasonName { get; set; } = "Episodes";

    /// <summary>
    /// Gets or sets the library (virtual folder) item ids Ronin is allowed to
    /// process. Empty means process nothing (fail-safe default): every task
    /// no-ops until an admin selects at least one library.
    /// </summary>
    public string[] LibraryIds { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// Gets or sets a value indicating whether season rows that no episode
    /// references (display-empty physical folders left behind by the merge)
    /// are hidden in the web UI. Presentation only; no library data changes.
    /// </summary>
    public bool HideEmptySeasons { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the merge/split tasks re-run
    /// specials placement for each series they modified, so chronological
    /// ordering is never stale after a re-org.
    /// </summary>
    public bool PlaceSpecialsAfterReorg { get; set; } = true;
}

/// <summary>
/// Modes for identifying anime series in the library.
/// </summary>
public enum AnimeIdentificationMode
{
    /// <summary>
    /// Identify anime series by Genre only.
    /// </summary>
    Genre,
    /// <summary>
    /// Identify anime series by Tag only.
    /// </summary>
    Tag,
    /// <summary>
    /// Identify anime series by Genre or Tag.
    /// </summary>
    GenreOrTag,
    /// <summary>
    /// Identify anime series by both Genre and Tag.
    /// </summary>
    GenreAndTag
}