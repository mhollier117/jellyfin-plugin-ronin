using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// One episode considered for reconciliation.
/// </summary>
/// <param name="Id">The episode item id.</param>
/// <param name="PathSeason">The season parsed from the file path (the aired-order coordinate).</param>
/// <param name="PathEpisode">The episode parsed from the file path.</param>
/// <param name="CurrentIndex">The episode's current IndexNumber, if any.</param>
public readonly record struct ReconcileItem(
    Guid Id, int PathSeason, int PathEpisode, int? CurrentIndex);

/// <summary>
/// Plans corrections for episodes ALREADY merged into season 1 whose absolute
/// number disagrees with the authority.
/// <para>
/// <see cref="MergePlan"/> sets <c>needsMove = ParentIndexNumber != 1</c>, so an
/// episode already in season 1 is a no-op and is never re-examined. Numbers
/// assigned by the pre-API resolvers therefore persist forever. A library audit
/// against TheTVDB on 2026-09-06 found 2,312 correct and 15 wrong - World
/// Trigger's season 3 uniformly +12, and one Mushoku Tensei episode -2 that was
/// additionally squatting on the slot a still-unmerged episode needed.
/// </para>
/// <para>
/// Renumbering existing content is precisely what the collision guard exists to
/// prevent, so this planner is ALL-OR-NOTHING: it either produces a provably
/// conflict-free layout for the whole series, or it declines and changes
/// nothing. It also never invents a number - an episode absent from the
/// authority map is left exactly as it is.
/// </para>
/// </summary>
public static class ReconcilePlan
{
    /// <summary>
    /// Computes the corrections to apply, or an empty list when unsafe.
    /// </summary>
    /// <param name="items">Every real episode of one series, with path-derived coordinates.</param>
    /// <param name="authority">(season, episode) -> absolute, from the provider API.</param>
    /// <returns>The (id, newIndex) writes to apply; empty when nothing to do or unsafe.</returns>
    public static IReadOnlyList<(Guid Id, int NewIndex)> Compute(
        IEnumerable<ReconcileItem> items,
        IReadOnlyDictionary<(int Season, int Episode), int> authority)
    {
        var none = Array.Empty<(Guid, int)>();
        if (items is null || authority is null || authority.Count == 0)
        {
            return none;
        }

        var seen = new HashSet<Guid>();
        var desired = new List<(Guid Id, int Index, bool Changed)>();

        foreach (var it in items)
        {
            if (!seen.Add(it.Id))
            {
                continue;
            }

            // Specials carry no position in the aired order.
            if (it.PathSeason <= 0 || it.PathEpisode <= 0)
            {
                if (it.CurrentIndex is > 0)
                {
                    desired.Add((it.Id, it.CurrentIndex.Value, false));
                }

                continue;
            }

            if (authority.TryGetValue((it.PathSeason, it.PathEpisode), out var abs) && abs > 0)
            {
                desired.Add((it.Id, abs, it.CurrentIndex != abs));
            }
            else if (it.CurrentIndex is > 0)
            {
                // No authority for this slot: keep what it has, and let it
                // participate in the conflict check so nothing is moved on top
                // of it.
                desired.Add((it.Id, it.CurrentIndex.Value, false));
            }
        }

        // The final layout must be a bijection. A swap is fine - intermediate
        // states collide but the caller writes the whole plan - whereas two
        // episodes genuinely wanting one slot means the inputs disagree, and
        // guessing a winner would silently destroy an episode's identity.
        var occupied = new HashSet<int>();
        foreach (var d in desired)
        {
            if (!occupied.Add(d.Index))
            {
                return none;
            }
        }

        var writes = new List<(Guid, int)>();
        foreach (var d in desired)
        {
            if (d.Changed)
            {
                writes.Add((d.Id, d.Index));
            }
        }

        return writes;
    }
}
