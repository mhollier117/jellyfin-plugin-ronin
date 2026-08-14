# Specials placement (1.0.7.0) — validation against Jellyfin 12 source

Traced 2026-08-14 against tag `v12.0-rc5` (nearest tag to the running server's
12.0.0 stable; no stable tag exists in jellyfin/jellyfin at trace time — the
files below had no changes between rc5 and release per the public changelog).
Server verified: `/System/Info` → 12.0.0, `DisplaySpecialsWithinSeasons=True`.

## Claim 1 — AirsBefore(S,E) places a special immediately before episode E

`Emby.Server.Implementations/Sorting/AiredEpisodeOrderComparer.cs`
`CompareEpisodeToSpecial`:

- primary key is the season: `ySeason = AirsAfterSeasonNumber ?? AirsBeforeSeasonNumber ?? -1`
- within the season, the special's `AirsBeforeEpisodeNumber` is compared to the
  episode's `IndexNumber`; **equal index → special sorts first** ("Special
  comes before episode"), otherwise numeric order.

So `AirsBefore(1, 5)` sorts after E4 and before E5 — exactly rule P3/P4 in
`SpecialPlacementPlanTests`. `AirsAfter(S)` sorts after every episode of S and
before season S+1 ("Special comes after episode" branch) — rule P6.

## Claim 2 — placement also controls season MEMBERSHIP in views

`MediaBrowser.Controller/Entities/TV/Episode.cs:59`:
`AiredSeasonNumber => AirsAfterSeasonNumber ?? AirsBeforeSeasonNumber ?? ParentIndexNumber`

`Series.FilterEpisodesBySeason` (Series.cs:457) admits an episode to a season's
list when `AiredSeasonNumber == seasonNumber` (with
`DisplaySpecialsWithinSeasons` on and season != 0), and
`GetSeasonEpisodes` (Series.cs:445) sorts non-special seasons by
`ItemSortBy.AiredEpisodeOrder` — the comparer from claim 1. The chain
filter → sort is therefore fully driven by the three fields the planner writes.

Note: the Specials (S0) season's own view sorts by SortName (same line 445,
ternary) — placement fields do not reorder the S0 folder itself. Irrelevant to
in-season presentation.

## Claim 3 — UpdateItemAsync(MetadataEdit) persists to NFO

`ItemUpdateType` (MediaBrowser.Controller/Library/ItemUpdateType.cs):
`MetadataEdit = 16 >= MetadataDownload = 8`.
`EpisodeNfoSaver.IsEnabledFor` (line 49): `SupportsLocalMetadata && Episode &&
updateType >= MinimumUpdateType`, where `MinimumUpdateType` is
`MetadataDownload` (BaseNfoSaver.cs:131-141). The saver writes
`airsafter_season`, `airsbefore_episode`, `airsbefore_season` (EpisodeNfoSaver
lines 83-100), skipping null/-1 — so cleared fields disappear from the NFO on
rewrite. This is the same durability anchor the merge task already relies on
(doc E3.1).

## Claim 4 — values survive normal refreshes; recurring runs heal the rest

`MediaBrowser.Providers/TV/EpisodeMetadataService.cs:93-106`: MergeData writes
each of the three fields only when `replaceData || !target.HasValue`. With
`ReplaceAllMetadata=false` (all refreshes the plugin and the server schedule
run), existing values are never overwritten.

**Durability gap found (accepted, self-healing):** the null half of a
placement IS overwritable. Example: planner writes `AirsBefore(1,5)` and
clears `AirsAfter`; a later refresh may FILL the null `AirsAfter` from a
remote provider that carries its own coarse placement. Because
`AiredSeasonNumber` and the comparer both prefer `AirsAfter` over
`AirsBefore`, the provider's value would then win. No code change: the
placement task is idempotent-by-comparison over all three fields, so any such
drift produces a plan Update on the next run and is reverted. This is a
designed argument for running `PlaceAnimeSpecialsTask` on a schedule rather
than once — consistent with the plugin's steady-state philosophy.

## Claim 5 — the runner's write recipe matches the server's

`PlaceSpecials.ExecuteForSeriesAsync` writes only the three ordering fields
via `UpdateItemAsync(episode, parent, MetadataEdit)` — the identical recipe
the merge task uses for its episode writes, previously validated in
`2026-08-01-merge-scoping-design-validation.md`. No ParentIndexNumber, no
SeasonId, no ParentId: placement never re-homes an item, so none of the
re-parenting risks from the 2026-08-01 incident apply.
