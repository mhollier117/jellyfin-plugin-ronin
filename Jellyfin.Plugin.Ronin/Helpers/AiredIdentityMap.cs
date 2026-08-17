using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// A real (merged) episode as the identity mapper sees it.
/// </summary>
/// <param name="AbsoluteNumber">The row's current index number in season 1.</param>
/// <param name="PremiereDate">Its air date, when known.</param>
public sealed record RealEpisodeRef(int AbsoluteNumber, DateTime? PremiereDate);

/// <summary>
/// A virtual episode - the server's cached provider structure for a
/// merged-away aired slot.
/// </summary>
/// <param name="SeasonNumber">Aired season number.</param>
/// <param name="EpisodeNumber">Aired episode number.</param>
/// <param name="PremiereDate">Its air date, when known.</param>
public sealed record VirtualEpisodeRef(int SeasonNumber, int EpisodeNumber, DateTime? PremiereDate);

/// <summary>
/// Recovers aired identity for merged (absolute-numbered) episodes by
/// JOINING on premiere dates: every merged-away aired slot exists as a
/// virtual row carrying (season, episode, date); the real row with the same
/// date IS that episode. No season sizes (the merged season-1 blob poisons
/// those), no per-episode provider ids (AniDB-only libraries lack them), no
/// network. Real rows matching no virtual are genuine aired-season-1
/// episodes, whose aired number equals their absolute number by
/// construction. Rules D1-D6 in AiredIdentityMapTests; anything ambiguous
/// resolves nobody rather than guessing.
/// </summary>
public static class AiredIdentityMap
{
    /// <summary>
    /// Builds the map absolute number -> aired (season, episode).
    /// </summary>
    /// <param name="realRows">Real episodes currently homed in season 1.</param>
    /// <param name="virtualRows">Virtual episodes of the series.</param>
    /// <returns>Resolved identities; unresolvable rows are absent.</returns>
    public static IReadOnlyDictionary<int, (int Season, int Episode)> Build(
        IReadOnlyCollection<RealEpisodeRef> realRows,
        IReadOnlyCollection<VirtualEpisodeRef> virtualRows)
    {
        var map = new Dictionary<int, (int, int)>();
        var virtByDate = virtualRows
            .Where(v => v.SeasonNumber > 0 && v.PremiereDate.HasValue)
            .GroupBy(v => v.PremiereDate!.Value.Date)
            .ToDictionary(g => g.Key, g => g.ToList());
        var realByDate = realRows
            .Where(r => r.PremiereDate.HasValue)
            .GroupBy(r => r.PremiereDate!.Value.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (date, reals) in realByDate)
        {
            if (!virtByDate.TryGetValue(date, out var virts))
            {
                // No merged-away slot aired this day: these are aired-S1
                // rows, whose aired number IS the absolute number (D2).
                foreach (var r in reals)
                {
                    map[r.AbsoluteNumber] = (1, r.AbsoluteNumber);
                }

                continue;
            }

            // D3/D4: within a shared date, pair sorted absolute order with
            // sorted aired order - but only when the counts agree; a
            // mismatch means the correspondence is ambiguous, and a wrong
            // re-home is worse than none.
            if (virts.Count != reals.Count)
            {
                continue;
            }

            var sortedReal = reals.OrderBy(r => r.AbsoluteNumber).ToList();
            var sortedVirt = virts.OrderBy(v => v.SeasonNumber)
                                  .ThenBy(v => v.EpisodeNumber).ToList();
            for (var i = 0; i < sortedReal.Count; i++)
            {
                map[sortedReal[i].AbsoluteNumber] =
                    (sortedVirt[i].SeasonNumber, sortedVirt[i].EpisodeNumber);
            }
        }

        // D7 chronology guard: absolutes are chronological, so no true
        // aired-S1 row can carry a HIGHER absolute number than any row
        // date-joined into a later season. An "S1" claim past that boundary
        // means the row's virtual twin lost its date - drop the claim
        // rather than mis-home the episode.
        var joinedAbs = map.Where(kv => kv.Value.Item1 > 1)
                           .Select(kv => kv.Key).ToList();
        if (joinedAbs.Count > 0)
        {
            var boundary = joinedAbs.Min();
            foreach (var abs in map.Where(kv => kv.Value.Item1 == 1
                                                && kv.Key > boundary)
                                   .Select(kv => kv.Key).ToList())
            {
                map.Remove(abs);
            }
        }

        return map;
    }
}
