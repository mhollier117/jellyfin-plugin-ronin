# Design validation: durable season merge (Fix #2) and library scoping (Fix #3)

Date: 2026-08-01/02 (research executed 2026-08-02)
Status: validated by 4 research/attack rounds; converged (round 4 produced zero new issues)
Evidence bases:
- Jellyfin server source v12.0-rc2 (matches live server): `C:\JF-Dev\jellyfin-src`
- Jellyfin server source v10.11.11 (ABI comparison): `C:\claude-code\jellyfin\src`
- Jellyfin 10.10.7 NuGet packages (ABI comparison): `C:\Users\Administrator\.nuget\packages\jellyfin.{controller,model}\10.10.7`
- Live server logs (read-only): `C:\ProgramData\Jellyfin\Server\log\log_20260801.log`
- Live library config (read-only): `C:\ProgramData\Jellyfin\Server\root\default\Anime\options.xml`
- On-disk media + NFO evidence: `D:\Anime` (read-only)
- Precedent plugin: `C:\JF-Dev\jellyfin-plugin-orientation`

All file:line citations below were verified against these trees. No plugin code was
implemented and the live server / its API / its service were not touched (data-recovery
scan in progress at time of writing).

---

## Verdicts (summary)

| Fix | Verdict | One-line rationale |
|---|---|---|
| #2 Durable merge | **GO-WITH-CAVEATS** | ParentId re-parenting is destructively reverted by scans (proven, see E2). The validated design never touches ParentId: it writes `ParentIndexNumber` + `SeasonId`/`SeasonName` (the server's own re-home recipe) and relies on the NFO save/read loop as the durability anchor — empirically already working on this server. Self-healing re-runs cover the remaining revert windows. Caveats: empty physical season stubs stay visible; external NFO rewrites revert until the next task run; series with no Season 1 item are skipped. |
| #3 Library scoping | **GO** | `BaseItem.GetAncestorIds()` ∩ configured `VirtualFolderInfo.ItemId` set is ABI-identical across 10.10.7 / 10.11 / 12.0 and has working precedent (orientation plugin, same machine). Single choke point: all four tasks enumerate via `CollectAnimeSeries`. Default = empty list = process nothing (fail-safe), with a migration note. |

**Answer to the critical scan-revert question:** yes for `ParentId` (destructively — the
scan re-creates the row under the physical season and wipes provider IDs/images, or
deletes the item outright), **no** for `ParentIndexNumber` on normal scans/refreshes,
and the NFO reader/saver pair makes `ParentIndexNumber=1` survive even
"Replace all metadata". Full trace in section E2/E3.

---

# Part 1 — Evidence

## E1. What links an Episode to a Season, and what the cascade delete keys on

### E1.1 Parent linkage

- `BaseItem.ParentId` is a plain stored `Guid` — `MediaBrowser.Controller\Entities\BaseItem.cs:481-482`; set only via `SetParent` (`BaseItem.cs:1391-1394`). `GetParent()` resolves it through the library manager cache (`BaseItem.cs:1014-1023`).
- `Episode.SeasonId` and `Episode.SeriesId` are **stored, denormalized** auto-properties — `MediaBrowser.Controller\Entities\TV\Episode.cs:134-137`; `SeasonName` at `Episode.cs:113-114`. They are real DB columns: `src\Jellyfin.Database\Jellyfin.Database.Implementations\Entities\BaseItemEntity.cs:163-165` (`SeasonId`, `SeriesId`), `:113-115` (`SeriesName`, `SeasonName`), round-tripped verbatim by `Jellyfin.Server.Implementations\Item\BaseItemMapper.cs:320-323` / `:146-149`.
- Runtime fallback only when the stored id is empty: `Episode.Season` (`Episode.cs:89-102`) calls `FindSeasonId()` (`Episode.cs:216-236`) which walks the `ParentId` chain first, then falls back to matching `ParentIndexNumber` against the series' Season children.
- `Episode.DisplayParentId => SeasonId` (`Episode.cs:64-65`) — this is what clients see as the episode's parent (`Emby.Server.Implementations\Dto\DtoService.cs:1074`, `:1348-1350`).
- `Episode.GetAncestorIds()` = ParentId chain ∪ collection folders ∪ **SeasonId** — `Episode.cs:274-286` (override of `BaseItem.cs:2628-2631`).

### E1.2 Who writes SeasonId/SeasonName

Exactly two server paths:
1. `EpisodeMetadataService.BeforeSaveInternal` — `MediaBrowser.Providers\TV\EpisodeMetadataService.cs:43-82`: `SeasonId = item.FindSeasonId()` (ParentId-chain first).
2. `SeriesMetadataService` end-of-series-refresh fix-up — `MediaBrowser.Providers\TV\SeriesMetadataService.cs:279-292`: finds the season whose `IndexNumber == episode.ParentIndexNumber`, sets `SeasonId` + `SeasonName`, saves with `UpdateToRepositoryAsync(ItemUpdateType.MetadataImport)`. **This is the server's own "move an episode to another season" code and it never touches ParentId.** These two fight when ParentId and ParentIndexNumber disagree; within one full series refresh the series-level fix-up runs last (bottom-up refresh, `Series.cs:300-312`), so the ParentIndexNumber-derived value wins at the end of each series refresh.

### E1.3 Cascade delete

- FK `FK_BaseItems_BaseItems_ParentId` ON DELETE CASCADE, added by migration `src\Jellyfin.Database\Jellyfin.Database.Providers.Sqlite\Migrations\20250913211637_AddProperParentChildRelationBaseItemWithCascade.cs:13-41` (after 4 orphan-purge passes); model: `ModelConfiguration\BaseItemConfiguration.cs:30`, snapshot `JellyfinDbModelSnapshot.cs:1517-1520`.
- `AncestorIds` table: composite PK (ItemId, ParentItemId), both FKs cascade — `Migrations\20241020103111_LibraryDbMigration.cs:122-150`, `AncestorIdConfiguration.cs:15-18`.
- `SeasonId`/`SeriesId` have **no FK at all** (deliberately commented out) — `BaseItemConfiguration.cs:17-21`; indexed only (`:72-74`). They can dangle.
- **The DB cascade is only a backstop.** `LibraryManager.DeleteItem` explicitly enumerates victims: `Emby.Server.Implementations\Library\LibraryManager.cs:545-547` (ParentId-recursive children) then `_persistenceService.DeleteItem([item.Id, ..children])` (`:596`). `ItemPersistenceService.DeleteItem` (`Jellyfin.Server.Implementations\Item\ItemPersistenceService.cs:57-66`) expands the set **again through the AncestorIds table** via `DescendantQueryHelper.GetOwnedDescendantIdsBatch` → `TraverseHierarchyDownOwned` (`src\Jellyfin.Database\Jellyfin.Database.Implementations\DescendantQueryHelper.cs:178-209`: `context.AncestorIds.WhereOneOrMany(currentFolders, e => e.ParentItemId)`), then hard-deletes every collected row (`ItemPersistenceService.cs:125-144`, `context.BaseItems...ExecuteDelete()` at `:139`). Verified first-hand.

**Consequence (root-cause refinement of the 2026-08-01 incident):** deleting a Season
destroys not only its ParentId-children but also **every episode whose AncestorIds rows
point at that season** — and `Episode.GetAncestorIds()` injects `SeasonId`
(`Episode.cs:274-286`). An episode parented to the series or to a *different* physical
season, but with a stale `SeasonId` referencing the deleted season, dies with it.
The shipped 1.0.4.0 guard (`Helpers/SeasonCleanup.cs` + `MergeSeasonsTask.cs:264-268`)
counts only `Parent = season` (a raw ParentId query, `BaseItemRepository.TranslateQuery.cs:234-237`)
— **it can read 0 while the delete still kills episodes.** This gap is closed by the new
design (Ronin stops deleting seasons entirely; see D2.4).

Incident log evidence (`log_20260801.log`): merge runs at 09:35, 10:19, 12:58, 14:55
(crashed on the since-fixed HtmlAgilityPack load, line 54229), 15:17. "Removing empty
season" was logged unconditionally by the pre-guard build. Series whose refresh
completed re-homing (Alice in Borderland, Bleach, Dr. STONE, Food Wars, Frieren, HxH,
Solo Leveling, Slime, World Trigger) lost nothing at season-delete time; Naruto
(seasons 2-5 deleted, 13:29-13:33) and Naruto Shippuden (seasons 2-22, 16:22-16:33)
lost 185 and ~468 entries — consistent with virtual/metadata seasons still referenced
via SeasonId/AncestorIds when deleted. Files were never touched
(`DeleteFileLocation = false`, `MergeSeasonsTask.cs:273`).

## E2. THE CRITICAL QUESTION — what the next scan does to a re-parented episode

Scenario: plugin sets `episode.ParentId = season1.Id` while the file stays in
`Season 02\`. Next library scan (`Folder.ValidateChildrenInternal2`):

1. A folder's "current children" are fetched **by ParentId** (`Folder.cs:298-307` → `Folder.cs:844-852` → `InternalItemsQuery.cs:440-452` → `TranslateQuery.cs:234-236`). After re-parenting, Season 02's set no longer contains the episode.
2. The file is still resolved under Season 02; the resolved object gets `Id = MD5(type + lowercased path)` (`LibraryManager.cs:782-803`) — **identical to the existing row's Id** — and `SetParent(args.Parent)` = Season 02 (`ResolverHelper.cs:70-76`).
3. Id lookup and the path-fallback map are built **only from that folder's own children** (`Folder.cs:444-459`, `:426-433`) → both miss → the episode is classified "brand new" (`Folder.cs:479-482`) → `LibraryManager.CreateItems` (`Folder.cs:555-558`).
4. `CreateItems` → `SaveItems` → `UpdateOrInsertItems` takes the *existing-Id* branch: it `ExecuteDelete()`s the row's provider-id, image and locked-field tables, then overwrites the whole entity from the freshly-resolved object (`ItemPersistenceService.cs:267-288`; `BaseItemMapper.cs:222` writes ParentId, `:233` writes ParentIndexNumber). Resolver output for an episode under a season folder has `ParentIndexNumber = null` (`EpisodeResolver.cs:74-84` only sets it when there is **no** season folder), so the row comes back with ParentId=Season02, ParentIndexNumber=null; the follow-up first-refresh fills ParentIndexNumber from the path (`LibraryManager.cs:3230-3238`; folder pattern `NamingOptions.cs:464`) → **2**.
5. Concurrently, Season 1's validation sees a DB child whose file is not in its folder and **deletes it** (`Folder.cs:486`, `:544-551` → `LibraryManager.DeleteItem`, DB row + cache purged `LibraryManager.cs:596-601`; media file survives via `DeleteFileLocation=false`).

**Conclusion: ParentId re-parenting is reverted by the next scan, destructively
(provider IDs/images wiped or item deleted+recreated), and the two season validations
race.** No server code other than `Folder.ValidateChildrenInternal2` +
`ResolverHelper.SetInitialItemValues` ever writes ParentId; nothing in
`MetadataService`/`SeriesMetadataService` does. Setting ParentId is therefore
**forbidden** in the design. (This also retroactively validates the commented-out
`// episode.ParentId = Guid.Empty;` at `MergeSeasonsTask.cs:150` — re-parenting to
"no parent" would orphan the row for the same reasons.)

### E2.1 What survives a scan when ParentId is NOT touched

For an item matched by Id under its unchanged parent, the resolver output is
**discarded** — only `UpdateFromResolvedItem` runs, which compares a tiny field set
(`IsInMixedFolder`, video parts/versions — `BaseItem.cs:1510-1521`,
`Video.cs:404-427`) and never touches ParentIndexNumber/SeasonId
(`Folder.cs:444-457`). **A plain scan does not revert ParentIndexNumber=1.**

## E3. What a metadata refresh does to ParentIndexNumber=1

- `MetadataService.MergeData` condition (v12 actual line): `MetadataService.cs:1088-1091` — `if (replaceData || !target.ParentIndexNumber.HasValue)`; IndexNumber same at `:1057-1060`. **Not lock-gated**: `MetadataField` (`MediaBrowser.Model\Entities\MetadataField.cs`) contains only `Cast, Genres, ProductionLocations, Studios, Tags, Name, Overview, Runtime, OfficialRating` — there is **no lockable field for index numbers**, and the `:1088`/`:1057` branches have no `lockedFields` guard. Field locking is not a viable mechanism.
- On a normal refresh, `temp` is seeded from the item itself (`MetadataService.cs:749-755` copies `item.ParentIndexNumber`), remote providers cannot override it (`MetadataService.cs:928` merges with `mergeMetadataSettings:false`; the Episode-specific override `EpisodeMetadataService.cs:113-121` requires it true; remote values are anyway echoes of the item's own number, e.g. `TmdbEpisodeProvider.cs:100/181`), and path re-parsing is gated: `Episode.BeforeMetadataRefresh` → `FillMissingEpisodeNumbersFromPath` (`Episode.cs:369-374`, `LibraryManager.cs:3144-3151`) only overwrites when `!ParentIndexNumber.HasValue || forceRefresh` (`LibraryManager.cs:3230-3238`). **Normal refresh: 1 survives.**
- `ReplaceAllMetadata` (`forceRefresh=true`) **does** overwrite from the path (`Season 02\...` → 2). BUT the local NFO provider merges after path parsing with `mergeMetadataSettings:true` (`MetadataService.cs:809` → `EpisodeMetadataService.cs:113-121`; parser `EpisodeNfoParser.cs:119-123`), and the final `MergeData(temp→item, replaceData:true)` (`MetadataService.cs:866-869`) writes the NFO's value. **If the sibling `.nfo` says `<season>1`, even Replace-All converges back to 1.**

### E3.1 The NFO anchor is live on this server (empirical)

- The Anime library has the Nfo saver AND reader explicitly enabled: `C:\ProgramData\Jellyfin\Server\root\default\Anime\options.xml:26-32` (`<MetadataSavers><string>Nfo</string>`, `<LocalMetadataReaderOrder><string>Nfo</string>`). `SaveLocalMetadata=false` (`:16`) is **irrelevant** here: when `libraryOptions.MetadataSavers` is non-null the enable check is membership in that list only — `ProviderManager.cs:842-887`, specifically the `else` branch at `:881-887`; the `IsSaveLocalMetadataEnabled` check (`:862`) lives in the `MetadataSavers is null` branch.
- `EpisodeNfoSaver` writes `<season>` from `ParentIndexNumber` (`EpisodeNfoSaver.cs:69-71`) and is enabled for `updateType >= MetadataDownload` (`EpisodeNfoSaver.cs:49-50`, `BaseNfoSaver.cs:131-141`). Ronin saves with `MetadataEdit` (≥ MetadataDownload) → NFO rewritten on every Ronin update. The server's own `MetadataImport` fix-up saves do **not** rewrite NFOs.
- Proof it already happened: 3,325 `.nfo` files exist under `D:\Anime`; the ones in `Season 02` folders already carry the merged values from the 2026-08-01 run — e.g. `D:\Anime\Dr. STONE (2019) [tvdbid-355774]\Season 02\...S02E01....nfo` contains `<season>1</season>` and absolute `<episode>25</episode>`; all sampled Solo Leveling `Season 02\*.nfo` contain `<season>1</season>`. These are Jellyfin-written files (lockdata/art/dateadded structure).

## E4. How season pages/counts actually resolve (why ParentIndexNumber is sufficient for display)

- `/Shows/{seriesId}/Episodes` and season pages load episodes by `SeriesPresentationUniqueKey` / ancestor-presentation-key, **never by ParentId or SeasonId** (`TvShowsController.cs:233-267`, `Series.cs:366-407`, `Season.cs:160-186`, `:196-224`; SQL translation `TranslateQuery.cs:1031-1041`).
- Membership is decided in memory by `Series.FilterEpisodesBySeason` (`Series.cs:433-457`): **rule 1 = `ParentIndexNumber` (or AiredSeasonNumber) == season.IndexNumber, checked first and sufficient**; rule 3 falls back to `episodeItem.Season` (SeasonId) presentation key. So `ParentIndexNumber=1` alone puts the episode on the Season 1 page; a stale SeasonId additionally shows it under the old season page until SeasonId is fixed (hence the design writes SeasonId too).
- `SeasonId` is additionally used for: client DTO parent (`DtoService.cs:1074`, `:1349`), "Latest TV" SQL grouping (`BaseItemRepository.Querying.cs:310-317`), and the extra AncestorIds row (`Episode.cs:274-286`).
- `UpdateItemAsync` persists everything and **recomputes AncestorIds + TopParentId on every save**, regardless of `ItemUpdateType` (`LibraryManager.cs:2544-2594`; `ItemPersistenceService.cs:244-265`, diff/write `:375-404`). `ItemUpdateType` gates only metadata savers/images/events (`LibraryManager.cs:2649-2657`, `ProviderManager.cs:864`).
- Season lists: `Series.GetSeasons` filters only `IsMissing` for users hiding missing episodes (`Series.cs:196-221`). `RemoveObsoleteSeasons` deletes **only virtual** seasons, and only when no physical season shares the number or the season lists zero episodes (`SeriesMetadataService.cs:110-146`; the emptiness check `virtualSeason.GetEpisodes().Count == 0` at `:132` goes through FilterEpisodesBySeason, so a stale-SeasonId episode still protects its season — the server's own cleanup is safe). Its delete call is `LibraryManager.DeleteItem` (`:136-143`), i.e. the AncestorIds-expanding delete — safe only because of that guard. **Empty physical seasons are never removed and remain visible.**

## E5. Library scoping APIs (ABI across 10.10.7 / 10.11 / 12.0)

- `BaseItem.GetAncestorIds()` — v12 `BaseItem.cs:2628-2631`, v10.11 `BaseItem.cs:2558-2561`, identical; returns ParentId-chain ids ∪ `GetCollectionFolders(this)` ids, so it includes the top-level library (CollectionFolder) id. `Series` does not override it; `Episode` adds SeasonId (`Episode.cs:274-282`).
- `ILibraryManager.GetCollectionFolders(BaseItem)` — v12 `ILibraryManager.cs:537/:545`, v10.11 `:486/:494`, 10.10.7 confirmed via NuGet XML doc (`MediaBrowser.Controller.xml:3433/:3440`). Implementation identical between 10.11 and 12 (`LibraryManager.cs` v12 `:2695-2741` vs 10.11 `:2257-2303`): walks parents up to AggregateFolder, then exact path / `PhysicalLocations` match (`:2736-2741`).
- `ILibraryManager.GetVirtualFolders()` — v12 `ILibraryManager.cs:193-195`, v10.11 `:167-169`, 10.10.7 XML `:3163`. `VirtualFolderInfo.ItemId` is a **string in "N" (dash-less) GUID format** (`MediaBrowser.Model\Entities\VirtualFolderInfo.cs:46`, byte-identical file in 10.11/12; populated at `LibraryManager.cs:1567-1575` v12 / `:1322-1323` 10.11; may be null for unresolved folders).
- `BaseItem.GetTopParent()` — v12 `BaseItem.cs:2633-2641` — exists everywhere but weaker (can return null; not guaranteed to be the ticked CollectionFolder). Not chosen.
- `ILibraryManager.IsScanRunning` — v12 `ILibraryManager.cs:54`, v10.11 `:53`.
- `InternalItemsQuery` gained/moved members between 10.11 and 12 (`LinkedChildAncestorIds` new at v12 `:369`); source-compatible per-target but a less stable surface — post-query in-memory filtering is preferred.
- Precedent (`C:\JF-Dev\jellyfin-plugin-orientation`, running on this very server): config stores `string[] LibraryIds` of N-format ItemIds (`PluginConfiguration.cs:10-14`), configPage enumerates `ApiClient.getJSON(ApiClient.getUrl("Library/VirtualFolders"))` into checkboxes normalizing dashes both ways (`configPage.html:32-60`), server side filters with `item.GetAncestorIds()` ∩ `Guid.TryParse`d config (`Services\LibraryScope.cs:8-36`), empty list ⇒ nothing processed (`OrientationLookup.cs:30`). Zero `#if` needed across the three targets for library resolution.

## E6. Plugin-side facts

- All four enumeration tasks route through `CollectAnimeSeries.Execute` — `Tasks\MergeSeasonsTask.cs:77`, `SplitSeasonsTask.cs:84`, `FillerUpdateTask.cs:98`, `FillerResetTask.cs:64`. No other code enumerates the library unscoped (remaining `GetItemList` calls are per-series/per-season child queries). `StartupTask` is UI-injection only. **One filter in `CollectAnimeSeries` scopes everything.**
- `MergeSeasonsTask.cs:135-140`: the idempotency skip (`ParentIndexNumber == 1` → continue) never repairs a stale `SeasonId` — episodes merged by number but not by SeasonId stay half-merged forever and keep a protective/deadly AncestorIds row on the old season.
- `MergeSeasonsTask.cs:191-196` passes the **episode itself** as the `parent` argument of `UpdateItemAsync`; the parent is used for Folder-cache invalidation (`LibraryManager.cs:2604-2608`), so this is a no-op — should pass `episode.GetParent()`.
- Physical layout reality (from logs + disk): three layouts coexist and all must work (hard requirement): (a) `Season NN\` folders per aired season (Dr. STONE, Solo Leveling, ...), (b) flat (Bleach, Hunter x Hunter), (c) everything in `Season 01\` with additional metadata-only seasons (Naruto, Naruto Shippuden, Boruto).
- AniDB page scraping gets HTTP 403 from this server; TheTVDB returns 200. Merge already tries TVDB first (`MergeSeasonsTask.cs:158-180`).

---

# Part 2 — Validated designs

## D1. Fix #2: self-healing merge (and the same rules for Split)

The merge is **explicitly self-healing, not unconditionally durable**: each converged
state survives normal scans and normal refreshes indefinitely, survives Replace-All
refreshes via the NFO anchor, and re-converges via task re-run for the residual
windows listed in R-risks.

### D2.1 Invariants

1. **Never write `Episode.ParentId`.** (E2 — destructively reverted.)
2. **Never call `ILibraryManager.DeleteItem` from Ronin, on anything.** (E1.3 — AncestorIds-expanding delete; the server's own guarded `RemoveObsoleteSeasons` handles virtual-season cleanup, E4.)
3. Every write is conditional (idempotent) and per-episode failure-isolated.
4. Skip work, never guess: unresolvable renumbering ⇒ skip that episode; missing Season 1 item ⇒ skip that series with a warning.

### D2.2 Per-series write sequence (merge)

```
series = scoped anime series (Fix #3)
if _libraryManager.IsScanRunning: log + abort run          (avoid racing ValidateChildren)
season1 = series' Season with IndexNumber == 1
if season1 is null: warn + skip series                     (rare; see R5)
episodes = GetItemList(Parent=series, Episode, Recursive, IsVirtualItem=false)
             .Where(e => e.ParentIndexNumber > 0)          (specials untouched)
for each episode:
    target = MergePlan.Compute(episode, season1, numberingContext)
    if target.NoOp: continue
    episode.ParentIndexNumber = 1                          (display + NFO + virtual-season logic; E4)
    if renumber needed && resolved: episode.IndexNumber = absolute
    if renumber needed && unresolved: revert nothing, skip episode (warn)
    episode.SeasonId  = season1.Id                          } mirror of the server's own
    episode.SeasonName = season1.Name                       } re-home, SeriesMetadataService.cs:279-292
    await UpdateItemAsync(episode, episode.GetParent(), ItemUpdateType.MetadataEdit, ct)
        -> persists row, recomputes AncestorIds (drops old-season row unless still ParentId-chained)  (E4)
        -> rewrites .nfo with <season>1 (+ <episode>) — the durability anchor              (E3.1)
after loop, if anything changed and RefreshSeriesAfterProcessed:
    await series.RefreshMetadata(Default, ReplaceAll=false)
        -> SeriesMetadataService removes now-empty *virtual* seasons safely (E4)
        -> final SeasonId fix-up re-asserts season1 for any stragglers (E1.2)
    (drop the ValidateChildren call — nothing was re-parented, it only adds risk/time)
```

Ordering vs season deletion: there is no Ronin season deletion anymore. Virtual season
cleanup is delegated to the server refresh, which runs after all episode writes and is
provably guarded (E4). Physical `Season NN` items remain as empty stubs (R1).

`MergePlan.Compute` (new pure, unit-testable helper) returns NoOp when
`ParentIndexNumber == 1 && SeasonId == season1.Id && (no renumber needed || IndexNumber already correct)`.
**The stale-SeasonId case (`ParentIndexNumber==1`, `SeasonId != season1.Id`) MUST emit an
update** — this repairs the half-merged state left by 1.0.4.0 and removes the lethal
stale AncestorIds rows (E1.3).

### D2.3 Absolute renumbering under the AniDB 403 constraint

- Keep the existing "already sequential" short-circuit but fix the heuristic: treat the
  numbering as absolute when all IndexNumbers are **distinct and strictly increasing
  across seasons** (gaps allowed — missing episodes are normal here), instead of
  requiring a gapless 1..N. Bleach and HxH (flat, sequential) then need zero scraping.
- TVDB is the primary source (unchanged). AniDB becomes a per-run circuit breaker:
  on the first HTTP 403, disable AniDB lookups for the remainder of the run and log once.
- If neither source yields a number for an episode that needs one: **skip the episode
  entirely** (no ParentIndexNumber write either), so Season 1 never accumulates
  colliding IndexNumbers. The series stays partially merged and converges on a later
  run — acceptable under the self-healing model, and strictly safer than the current
  behavior (merge-without-renumber ⇒ duplicate "Episode 1" entries).

### D2.4 SeasonCleanup and the guard gap

`SeasonCleanup.ShouldDeleteSeason` stays as a tested tripwire but the merge task stops
calling `DeleteItem` altogether (invariant 2). The unit suite additionally asserts the
task never invokes `DeleteItem` on a mocked `ILibraryManager`. Rationale: the 1.0.4.0
guard is provably insufficient (E1.3 — ParentId-child count misses AncestorIds/SeasonId
victims), and any "count everything correctly then delete" approach re-implements
server logic that `RemoveObsoleteSeasons` already runs with the correct guard.

### D2.5 Split task alignment

`SplitSeasonsTask` gets the same treatment: never ParentId (already true), set
`ParentIndexNumber = airedSeason` and, when a season item with that number exists,
`SeasonId`/`SeasonName`; when it does not yet exist, write only `ParentIndexNumber` and
let the series refresh create the virtual season and assign SeasonId
(`SeriesMetadataService.CreateSeasonsAsync`, `SeriesMetadataService.cs:251-291`;
episodes inside physical season folders don't trigger virtual-season creation —
`NeedsVirtualSeason`, `:205-226` — but for the split direction the physical folder
seasons normally already exist). Saves use `MetadataEdit` so NFOs anchor the split
numbers too. Both directions (merge and split) are covered by the test matrix for all
three physical layouts (hard requirement).

## D3. Fix #3: library scoping

### D3.1 Mechanism (a)

`RoninLibraryScope.IsInScope(configuredIds, series.GetAncestorIds())` — set
intersection after `Guid.TryParse` (accepts both "N" and dashed formats), exactly the
orientation plugin's proven pattern (E5). Applied inside `CollectAnimeSeries.Execute`
before the genre/tag check, which automatically scopes Merge, Split, FillerUpdate and
FillerReset (E6). No `#if` needed on any target; `GetAncestorIds`,
`GetCollectionFolders`, `GetVirtualFolders`, `VirtualFolderInfo.ItemId` are
signature-identical across 10.10.7 / 10.11 / 12.0 (E5). In-memory filtering is chosen
over `InternalItemsQuery.AncestorIds` because the query type's surface moved between
versions and series counts are tiny.

### D3.2 Config shape (b)

`PluginConfiguration.LibraryIds : string[]`, default `Array.Empty<string>()`, storing
`VirtualFolderInfo.ItemId` values normalized to dash-less lowercase. configPage adds a
checkbox list rendered from `GET Library/VirtualFolders` (orientation precedent,
`configPage.html:32-60`), normalizing ids on both read and save; entries with null
`ItemId` are skipped.

**Default behavior decision: empty list = process nothing (fail-safe), recommended.**
Justification: every Ronin enumeration task is either destructive re-organization or
bulk tag mutation, and the incident that motivates this fix (Alice in Borderland, a
live-action D:\TV series merged via a stray tag) is precisely a scope-everything
failure. After a data-loss event, fail-safe beats fail-compatible. Each task logs a
single clear warning ("Ronin: no libraries selected — nothing to do; select libraries
in the plugin settings") and exits successfully.
**Migration note (required in release notes):** upgrading installs will see all Ronin
tasks no-op until an admin ticks their anime library once. The alternative
(empty = all, "compatible") was rejected: it silently preserves the exact failure mode
this fix exists to remove, and the plugin has one known deployment (this server).

### D3.3 Coverage (c)

Confirmed complete: the four tasks all enumerate exclusively via
`CollectAnimeSeries.Execute` (E6); their inner queries are parented to
already-scoped items. StartupTask registers UI injection only. No other entry points.

---

# Part 3 — TDD test matrix

Per repo policy, every fix ships red→green: each unit test below must first fail
against current code.

## U. Unit tests (tests/Jellyfin.Plugin.Ronin.Tests)

Scoping:
1. `LibraryScope_EmptyConfig_ProcessesNothing` — empty `LibraryIds` ⇒ false for any ancestor set. (RED today: no scoping exists.)
2. `LibraryScope_MatchingAncestor_True` / `NonMatching_False`.
3. `LibraryScope_ParsesNFormat_And_DashedFormat`; unparseable strings ignored.
4. `CollectAnimeSeries_FiltersByLibrary_BeforeGenreTag` — refactor `IsAnime`+scope into a pure function taking `(genres, tags, ancestorIds, config)`; matrix over the four `AnimeIdentificationMode`s × in/out of scope. Regression pin: a series with tag "Anime" outside the scoped library (Alice in Borderland shape) is excluded.

Merge plan (new pure helper):
5. `MergePlan_AlreadyConverged_NoOp` — PIN=1, SeasonId=season1, numbering fine ⇒ no write.
6. `MergePlan_StaleSeasonId_EmitsUpdate` — PIN=1 but SeasonId=oldSeason ⇒ SeasonId/SeasonName write. (RED today: `MergeSeasonsTask.cs:135` skips.) **This is the incident-regression test.**
7. `MergePlan_Specials_Untouched` — PIN=0 or null excluded.
8. `MergePlan_SetsSeasonIdAndName_WithParentIndexNumber` — full write set for PIN>1.
9. `MergePlan_NoSeasonOne_SkipsSeries` with warning outcome.
10. `Numbering_DistinctIncreasingWithGaps_IsAbsolute_NoScrape` (RED today: gapless-1..N required).
11. `Numbering_DuplicateOnes_RequiresRenumber`.
12. `MergePlan_RenumberUnresolved_SkipsEpisodeEntirely` — no partial write. (RED today: merges without renumber.)
13. `AniDb_CircuitBreaker_OpensOn403_NoFurtherCalls` (HttpRetry/resolver level, using the existing fake-handler rig).

Safety invariants:
14. `MergeTask_NeverCallsDeleteItem` — mocked `ILibraryManager` records calls; zero `DeleteItem` invocations across a merge run containing empty and non-empty, physical and virtual seasons. (RED today.)
15. `SeasonCleanup_PhysicalSeason_NeverDeletable` — extend/replace `ShouldDeleteSeason` contract (tripwire retained).
16. `UpdateItemAsync_ReceivesEpisodePhysicalParent` — parent argument is `episode.GetParent()`, not the episode. (RED today.)
17. `MergeTask_AbortsWhenScanRunning` — `IsScanRunning=true` ⇒ no writes.

Layout matrix (hard requirement — merged AND split support):
18. Parameterized fixture over the three layouts: (a) `Season NN` physical folders, (b) flat, (c) `Season 01` physical + virtual seasons 2+. For each: merge plan produces converged state; split plan (aired numbers from a stubbed resolver) produces the inverse; running merge twice is a no-op the second time (idempotency).

## E2E (live server) — ONLY after the data-recovery scan completes, and with explicit user approval for each task run / any restart (standing rule: no unprompted restarts)

Preconditions: recovery verified (Naruto ≈220 entries, Naruto Shippuden ≈508; counts
via the admin dashboard or item counts per series), Ronin build with both fixes
deployed, scoping configured = Anime library only.

1. **Scoping negative (Alice in Borderland, D:\TV):** run FillerUpdate (benign task) with only D:\Anime ticked. Expect: task log enumerates no D:\TV series; Alice untouched. Then untick everything: expect the "no libraries selected" warning and zero series processed.
2. **Merge, physical-folders layout (Solo Leveling):** before: episodes PIN per aired season (post-recovery state may already be merged via NFO anchor — record actual before-state). Run merge. Expect after: all episodes PIN=1, SeasonId=Season 1 item, Season 1 page lists all episodes in absolute order; `Season 02\*.nfo` contain `<season>1</season>`; empty Season 2 stub visible on the series page (accepted, R1); episode count unchanged.
3. **Scan-stability:** trigger a library scan. Expect: no episode moves, no deletions in the log, PIN still 1, counts unchanged. Then refresh one episode (default mode): unchanged. Then Replace-All-metadata on one episode: PIN ends at 1 (NFO anchor; transient 2 mid-refresh acceptable).
4. **Merge, Season-01+virtual layout (Naruto Shippuden) — the incident regression:** record episode count before. Run merge. Expect: count identical after; virtual seasons 2+ removed by the server refresh ("Removing virtual season" log lines from `SeriesMetadataService`, not from Ronin); zero Ronin "Removing … season" lines; all episodes on Season 1 page.
5. **Merge, flat layout (Bleach):** expect zero scrape requests (sequential short-circuit), no-op or SeasonId-repair-only writes.
6. **Split round-trip (Dr. STONE):** run split; expect episodes distributed to aired seasons, NFOs updated to the split numbers; then merge again; expect convergence back with no losses. Layout matrix satisfied live.
7. **Idempotency:** immediately re-run merge; expect "0 changes" style run.

---

# Part 4 — Attack rounds (full record)

## Round 1 — research + first draft
Research fanned out over the v12 tree (parent linkage/cascade; re-parent write path;
scan/refresh revert; scoping ABI) plus plugin/log/disk evidence. First draft mirrored
the server's own re-home (write ParentId + SeasonId + PIN, then UpdateItemAsync;
strengthen the season-delete guard; scope via GetAncestorIds).

## Round 2 — attacks on the draft (each resolved or accepted)
- **A1 "Set ParentId so the hierarchy is truly consistent"** — REFUTED: E2 proves scans revert it destructively (`Folder.cs:479-482` + `ItemPersistenceService.cs:267-288` + `Folder.cs:544-551`). Design changed to never write ParentId. Dead end recorded: there is no correct ParentId value for a file that physically lives in `Season 02\` other than the Season 02 item itself.
- **A2 "Lock the fields instead"** — REFUTED: `MetadataField` has no index-number member and `MergeData`'s `:1057/:1088` branches are not lock-gated; whole-item lock (`IsLocked`) doesn't gate ValidateChildren and is itself path-derived (`ResolverHelper.cs:81-82`). Not viable.
- **A3 "Intercept with a custom resolver"** — REJECTED as design: a plugin `IItemResolver` only shapes item creation from paths; it cannot stop `ValidateChildrenInternal2`'s child-set diffing, and faking resolution output would desync DB from disk. (Capability exists — `LibraryManager.EntityResolvers` — but it is the wrong lever.)
- **A4 "The 1.0.4.0 guard is enough for deletions"** — REFUTED: guard counts ParentId children only; `DeleteItem` also kills AncestorIds-descendants incl. stale-SeasonId episodes (E1.3, verified first-hand). Design changed: Ronin performs no deletions at all; server's own `RemoveObsoleteSeasons` is provably guarded (E4).
- **A5 "Skip-if-PIN==1 idempotency is fine"** — REFUTED: leaves stale SeasonId half-merges (the current live state) and lethal stale ancestor rows. MergePlan must repair SeasonId (test U6).
- **A6 "NFO files will fight the merge"** — INVERTED into the anchor: savers+readers are both enabled for Anime (`options.xml:26-32`, `ProviderManager.cs:881-887`), Ronin's `MetadataEdit` saves rewrite `<season>` (`EpisodeNfoSaver.cs:69-71`), and disk evidence shows Season-02 NFOs already at `<season>1</season>`. Residual R2 covers external rewrites.
- **A7 "Renumbering failure ⇒ merge anyway (current behavior)"** — CHANGED: skip the episode to avoid IndexNumber collisions in Season 1 (D2.3), self-healing covers convergence.
- **A8 "Scoping should filter in the DB query"** — REJECTED for `InternalItemsQuery` surface drift between 10.11/12; in-memory intersection is ABI-stable and unit-testable (E5).
- **A9 "Empty = process all is friendlier"** — REJECTED with justification recorded (D3.2).

## Round 3 — attacks on the revised design (new items found, all closed)
- **B1 Series with no Season 1 item** (all files in `Season 02+`): `CreateSeasonsAsync` won't create a virtual Season 1 for episodes inside physical season folders (`NeedsVirtualSeason`, `SeriesMetadataService.cs:205-226`) ⇒ SeasonId write has no target. Closed: skip series + warn (D2.2, test U9). Creating Season items from the plugin was considered and rejected (server owns season lifecycle).
- **B2 Sequential-with-gaps misdetection** causes pointless scraping on libraries with missing episodes. Closed: distinct-and-increasing heuristic (D2.3, test U10).
- **B3 `UpdateItemAsync(episode, episode, …)`** parent-arg bug found in current code (E6). Closed: pass `GetParent()` (test U16).
- **B4 Race with a concurrently running scan** (ValidateChildren deleting/recreating while Ronin writes). Closed: `IsScanRunning` abort (ABI-verified E5, test U17). Residual: a scan starting mid-run is untreated — writes are per-episode atomic and idempotent, next run converges (accepted, R4).
- **B5 Season 1 rename ("Episodes") durability**: same anchor logic applies (`SeasonNfoSaver.cs:70` writes season.nfo on MetadataEdit); Replace-All may transiently restore "Season 1" until re-run. Accepted as cosmetic (R3).
- **B6 Does the server's virtual-season cleanup kill stale-SeasonId episodes?** — checked and REFUTED: its emptiness test goes through FilterEpisodesBySeason rule 3, which counts stale-SeasonId episodes, so such a season is kept, not deleted (E4).
- **B7 10.10.7 ABI of `Episode.SeasonId`/`SeasonName`** — properties carry no XML doc comments so absence from the package XML proves nothing; they exist in the 10.11 tree (`Episode.cs:116/:136`) and have been stable API; the 10.10.7 target build compiling is the definitive gate (already part of the release pipeline).

## Round 4 — re-attack of all Round 2/3 closures
No new issues. Convergence declared. (Total: 4 rounds.)

---

# Part 5 — Residual risks (accepted, documented)

- **R1 Empty physical season stubs.** `Season NN` items backed by folders are never removed (`SeriesMetadataService.cs:117-132`) and `GetSeasons` shows them (`Series.cs:196-221`). Merged series display "Episodes" plus empty `Season 2..N` tiles. Only file moves could remove them, and the Sonarr layout is off-limits. Cosmetic; verified in E2E step 2.
- **R2 External NFO rewrites.** If Sonarr (or anything else) rewrites an episode `.nfo` with the physical season number, the next refresh reverts that episode's PIN; the next Ronin run re-converges and re-anchors. Frequency bounded by import/upgrade events.
- **R3 Replace-All refresh with a missing/deleted NFO** reverts PIN to the path-derived number until the next Ronin run (no anchor to restore from). Self-healing; schedule the merge task periodically if drift is observed.
- **R4 Scan starting mid-run** (B4): individual episodes may be refreshed between Ronin's write and the series refresh; state is per-episode consistent and converges next run.
- **R5 No-Season-1 series are skipped** (B1): none currently known in the library; surfaced via warning log if one appears.
- **R6 SeasonId oscillation between refresh layers.** An episode-only refresh sets `SeasonId = FindSeasonId()` (ParentId-chain ⇒ physical season, `EpisodeMetadataService.cs:68-72`); the next full series refresh re-asserts the PIN-matching season (`SeriesMetadataService.cs:279-292`). Between the two, the episode can transiently appear under the old season page via FilterEpisodesBySeason rule 3. Bounded and self-correcting; Season 1 listing (rule 1) is never affected.
- **R7 Cascade FK pragma.** Nothing in the server explicitly enables `PRAGMA foreign_keys` outside `PurgeDatabase` (`PragmaConnectionInterceptor.cs:80-107`, `SqliteDatabaseProvider.cs:202-207`); deletion safety therefore rests on the explicit `ExecuteDelete` fan-out (E1.3), which is what the design defends against. No dependency taken on FK behavior.
- **R8 Recovery interaction.** The ongoing data-recovery scan recreates episodes from disk; because Season-02 NFOs already say `<season>1</season>`, recovered episodes will come back merged for the affected series. E2E step 2 records the actual post-recovery before-state instead of assuming one.

---

# Appendix: current-code citations (fork)

- `Jellyfin.Plugin.Ronin\Tasks\MergeSeasonsTask.cs:149-150` — PIN write + commented ParentId reset; `:135-140` skip branch (stale-SeasonId gap); `:191-196` UpdateItemAsync parent-arg; `:231-232` ValidateChildren+RefreshMetadata; `:264-273` guarded delete (to be removed per D2.4).
- `Jellyfin.Plugin.Ronin\Helpers\SeasonCleanup.cs:19-30` — 1.0.4.0 guard (insufficient per E1.3, retained as tripwire).
- `Jellyfin.Plugin.Ronin\Helpers\CollectAnimeSeries.cs:24-39` — unscoped enumeration; single insertion point for Fix #3.
- `Jellyfin.Plugin.Ronin\Tasks\SplitSeasonsTask.cs:139-156` — split PIN writes (D2.5 alignment target).
