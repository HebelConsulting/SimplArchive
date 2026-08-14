using System.Collections.Generic;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Moving one entry of an ordered list up or down — the arithmetic behind both reorder dialogs (issue #487).
/// </summary>
/// <remarks>
/// One implementation rather than a copy per dialog. The sort-pages and join-items dialogs do exactly the same
/// thing to different element types, which is what a type parameter is for; copies drift, and the one that
/// silently clamps differently is the one nobody notices.
/// </remarks>
public static class ListOrder
{
    /// <summary>
    /// Moves the entry at <paramref name="index"/> by <paramref name="delta"/> places and returns where it
    /// ended up — or the original index when the move would fall off either end, so the caller's selection
    /// stays on the same entry.
    /// </summary>
    /// <remarks>
    /// Returning the new index is the point: the selection has to follow the ENTRY, not the slot. After moving
    /// something the user usually moves it again, and a selection that stayed put would move a different entry
    /// on the second click.
    /// </remarks>
    public static int Move<T>(IList<T> items, int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || index >= items.Count || target < 0 || target >= items.Count)
        {
            return index;
        }

        var moved = items[index];
        items.RemoveAt(index);
        items.Insert(target, moved);
        return target;
    }
}
