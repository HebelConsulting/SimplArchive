namespace SimplArchive.Client.Services;

/// <summary>
/// Moving one entry of an ordered list up or down — the arithmetic behind both reorder dialogs (issue #487).
/// </summary>
/// <remarks>
/// One implementation rather than a copy per dialog, for the reason copies always drift: the one that clamps
/// differently is the one nobody notices. The desktop has its own twin of this (its own project, and the two
/// clients share only the localization assembly) — two implementations for two clients, not four for four
/// dialogs.
/// </remarks>
public static class ListOrder
{
    /// <summary>
    /// Moves the entry at <paramref name="index"/> by <paramref name="delta"/> places and returns where it
    /// ended up — or the original index when the move would fall off either end.
    /// </summary>
    /// <remarks>
    /// Returning the new index is the point: the selection follows the ENTRY, not the slot. After moving
    /// something the user usually moves it again, and a selection that stayed put would move a different entry
    /// on the second click.
    /// </remarks>
    public static int Move<T>(List<T> items, int index, int delta)
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
