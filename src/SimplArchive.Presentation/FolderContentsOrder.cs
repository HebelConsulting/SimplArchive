namespace SimplArchive.Presentation;

/// <summary>
/// The order a folder's contents are listed in, answered identically by the desktop, the web and the phone (ADRs
/// 0650/0651; ADR "Per-folder contents sort order"; issue #339). Folders come first, alphabetical; then documents
/// in the folder's stored sort order (0 = name, 1 = document date, the default, 2 = filing time), ties by name.
/// </summary>
/// <remarks>
/// One implementation for three clients (ADR 0877): the desktop and the web each held a copy, and the phone, the
/// third, listed the server's raw order (creation), which the owner found in a Contracts folder on 2026-10-10.
/// A client's column-header sort is its own ephemeral override and stays with the client.
/// </remarks>
public static class FolderContentsOrder
{
    /// <summary>What the order reads from a row.</summary>
    public readonly record struct Keys(bool IsFolder, string Name, DateOnly? DocumentDate, TimeOnly? DocumentTime, DateTimeOffset? Filed);

    /// <summary>The rows in listing order. Unknown sort orders fall back to name.</summary>
    public static IEnumerable<T> Arrange<T>(IEnumerable<T> rows, int sortOrder, Func<T, Keys> keys)
    {
        var keyed = rows.Select(r => (Row: r, Keys: keys(r))).ToList();
        var folders = keyed.Where(k => k.Keys.IsFolder).OrderBy(k => k.Keys.Name, StringComparer.OrdinalIgnoreCase);
        var documents = keyed.Where(k => !k.Keys.IsFolder);
        var ordered = sortOrder switch
        {
            1 => documents.OrderBy(k => k.Keys.DocumentDate ?? DateOnly.MinValue).ThenBy(k => k.Keys.DocumentTime ?? TimeOnly.MinValue)
                .ThenBy(k => k.Keys.Name, StringComparer.OrdinalIgnoreCase),
            2 => documents.OrderBy(k => k.Keys.Filed ?? DateTimeOffset.MinValue).ThenBy(k => k.Keys.Name, StringComparer.OrdinalIgnoreCase),
            _ => documents.OrderBy(k => k.Keys.Name, StringComparer.OrdinalIgnoreCase),
        };
        return folders.Concat(ordered).Select(k => k.Row);
    }
}
