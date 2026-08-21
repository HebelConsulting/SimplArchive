using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>One remembered node: which folder was open, and under which parent.</summary>
/// <remarks>
/// The parent is stored, and it is not decoration — it is what makes PRUNING safe. A folder can be missing
/// from the restored tree for two very different reasons: it was deleted, or an ancestor is collapsed so the
/// tree never walked that far. Dropping every id that failed to turn up would silently forget the second case,
/// so a user who collapses one branch loses everything they had open inside it.
/// <para>
/// With the parent recorded the two are distinguishable: an entry is stale only when its parent WAS expanded
/// during the restore and the child was not among the children that came back. Anything under a branch the
/// restore never opened is simply left alone.
/// </para>
/// </remarks>
public sealed record ExpandedNode(Guid Id, Guid? ParentId);

/// <summary>
/// The expanded/collapsed shape of the Repositories tree, per server and user, remembered between sessions.
/// </summary>
/// <remarks>
/// <para>
/// Scoped by <c>{apiRootUrl}|{user}</c> because the desktop holds several server profiles and a machine can be
/// shared. Folder ids are globally unique so a single flat set would not collide — but it would put one
/// account's tree shape in another's file, and switching servers would restore a set that resolves to nothing.
/// </para>
/// <para>
/// Best-effort IO throughout, like <see cref="LayoutSettingsStore"/>: an unreadable or missing file yields an
/// empty set and a failed write is swallowed. A tree that opens flat is a small disappointment; a client that
/// refuses to start because it could not read a convenience file is not.
/// </para>
/// </remarks>
public static class TreeExpansionStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SimplArchive",
        "desktop-tree.json");

    /// <summary>Overridable so a test writes to a throwaway file rather than the developer's own state.</summary>
    public static string? PathOverride { get; set; }

    private static string Path_ => PathOverride ?? FilePath;

    /// <summary>The key for one server-and-user context. Lower-cased so a differently-typed email still matches.</summary>
    public static string KeyFor(string apiRootUrl, string user) =>
        $"{apiRootUrl.TrimEnd('/')}|{user}".ToLowerInvariant();

    public static IReadOnlyList<ExpandedNode> Load(string key)
    {
        try
        {
            return !File.Exists(Path_)
                ? []
                : (JsonSerializer.Deserialize<Dictionary<string, List<ExpandedNode>>>(File.ReadAllText(Path_))
                   ?? []).GetValueOrDefault(key, []);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Replaces this context's remembered set, leaving every other context in the file untouched.
    /// </summary>
    /// <remarks>
    /// Read-modify-write rather than holding the whole file in memory: two windows against different servers
    /// would otherwise each write back their own view and drop the other's.
    /// </remarks>
    public static void Save(string key, IEnumerable<ExpandedNode> nodes)
    {
        try
        {
            var all = File.Exists(Path_)
                ? JsonSerializer.Deserialize<Dictionary<string, List<ExpandedNode>>>(File.ReadAllText(Path_)) ?? []
                : [];

            var list = nodes.Distinct().ToList();
            if (list.Count == 0)
            {
                all.Remove(key); // an empty context is an absent one — no empty arrays accumulating per server
            }
            else
            {
                all[key] = list;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Remembering the tree is a convenience; losing it must never surface as an error.
        }
    }
}
