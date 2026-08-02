using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Decides whether an item belongs to one of the admin-selected libraries.
/// Set intersection of the configured library ids (tolerantly Guid-parsed;
/// dashed and "N" formats accepted, malformed entries ignored) with the
/// item's ancestor ids. Empty configuration means nothing is in scope
/// (fail-safe default, design doc D3.2).
/// </summary>
public static class LibraryScope
{
    /// <summary>
    /// Determines whether an item with the given ancestors is inside the
    /// configured library scope.
    /// </summary>
    /// <param name="configuredLibraryIds">Configured library item ids as strings.</param>
    /// <param name="ancestorIds">The item's ancestor ids (BaseItem.GetAncestorIds()).</param>
    /// <returns>True when the item is in scope.</returns>
    public static bool IsInScope(IReadOnlyCollection<string>? configuredLibraryIds, IEnumerable<Guid> ancestorIds)
    {
        if (configuredLibraryIds is null || configuredLibraryIds.Count == 0)
        {
            return false;
        }

        var configured = new HashSet<Guid>();
        foreach (var raw in configuredLibraryIds)
        {
            if (Guid.TryParse(raw, out var parsed))
            {
                configured.Add(parsed);
            }
        }

        if (configured.Count == 0)
        {
            return false;
        }

        foreach (var ancestor in ancestorIds)
        {
            if (configured.Contains(ancestor))
            {
                return true;
            }
        }

        return false;
    }
}
