using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Parses a TheTVDB v4 <c>/series/{id}/episodes/{season-type}</c> payload into a
/// (season, episode) -> absolute-number map.
/// <para>
/// This is the only resolver that stays correct when the library has gaps. The
/// local-order fallback can count only episodes actually held, so a series
/// missing two episodes produces absolute numbers two lower than anything
/// already resolved remotely - and the merge then collides. Measured
/// 2026-09-06 on Mushoku Tensei: S02E23 was already merged as absolute 48 by a
/// remote lookup, while the local order computed 46 for it, leaving three
/// episodes permanently stuck behind the collision guard.
/// </para>
/// <para>
/// The API returns the complete aired order including episodes the user does
/// not own, so every episode resolves from the same scheme regardless of what
/// is on disk.
/// </para>
/// </summary>
public static class TvdbAbsoluteMap
{
    /// <summary>
    /// Parses the episodes payload into a (season, episode) -> absolute map.
    /// </summary>
    /// <param name="json">The raw JSON body returned by the episodes endpoint.</param>
    /// <returns>The map; empty when the payload carries no usable episodes.</returns>
    public static IReadOnlyDictionary<(int Season, int Episode), int> Parse(string? json)
    {
        var map = new Dictionary<(int, int), int>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return map;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // A truncated or HTML error body must never take the task down;
            // the caller falls back to the next resolver.
            return map;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return map;
            }

            // The payload nests the list under data.episodes; some responses
            // return data as the array directly.
            JsonElement episodes;
            if (data.ValueKind == JsonValueKind.Array)
            {
                episodes = data;
            }
            else if (!data.TryGetProperty("episodes", out episodes)
                     || episodes.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            foreach (var ep in episodes.EnumerateArray())
            {
                if (ep.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var season = ReadInt(ep, "seasonNumber");
                var number = ReadInt(ep, "number");
                var absolute = ReadInt(ep, "absoluteNumber");

                // Specials (season 0) carry no absolute position in the aired
                // order and must never be renumbered.
                if (season is null or <= 0 || number is null or <= 0
                    || absolute is null or <= 0)
                {
                    continue;
                }

                // First writer wins: the endpoint can repeat an episode across
                // alternate orderings, and silently overwriting would make the
                // result depend on payload order.
                map.TryAdd((season.Value, number.Value), absolute.Value);
            }
        }

        return map;
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => null,
        };
    }
}
