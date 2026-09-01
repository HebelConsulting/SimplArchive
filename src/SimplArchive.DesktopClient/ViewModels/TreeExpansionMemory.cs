using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// Remembers which folders the user had open, and reopens them next session.
/// </summary>
/// <remarks>
/// <para>
/// Its own class rather than another hundred lines in <c>MainWindowViewModel</c>, which is on the 1000-line
/// standing-debt list: "the tree's shape between sessions" is one responsibility with its own state, its own
/// file and its own rules about what counts as gone. The view-model says which context it is in and hands over
/// the roots.
/// </para>
/// <para>
/// Scoped per server AND user (<see cref="TreeExpansionStore.KeyFor"/>): the client holds several server
/// profiles and a machine can be shared. Folder ids would not collide across servers — they are GUIDs — but a
/// single set would put one account's tree shape in another's file and restore ids that resolve to nothing.
/// </para>
/// </remarks>
public sealed class TreeExpansionMemory
{
    private readonly HashSet<ExpandedNode> _expanded = [];
    private string _key = string.Empty;
    private bool _restoring;

    /// <summary>Points the memory at one server-and-user context; call before recording or restoring.</summary>
    public void Use(string apiRootUrl, string user) => _key = TreeExpansionStore.KeyFor(apiRootUrl, user);

    /// <summary>Notes that a node opened or closed. Wired onto the ROOTS; descendants inherit it as they load.</summary>
    public void Record(TreeNodeViewModel node, bool expanded)
    {
        // While restoring, every node reopened would write the file back one node at a time — and record as
        // "the user opened this" something the user opened in a previous session. Nothing to learn, much to
        // write.
        if (_restoring || node.Id == Guid.Empty || _key.Length == 0)
        {
            return;
        }

        var entry = new ExpandedNode(node.Id, node.Parent?.Id);
        if (expanded)
        {
            _expanded.Add(entry);
        }
        else
        {
            // ONLY this node. A collapsed branch keeps its descendants' entries, so reopening it finds the
            // shape left inside — which is the difference between remembering a tree and snapshotting one.
            _expanded.Remove(entry);
        }

        TreeExpansionStore.Save(_key, _expanded);
    }

    /// <summary>Reopens the tree as it was left, and forgets folders that are genuinely gone.</summary>
    /// <remarks>
    /// <para>
    /// Breadth-first, level by level, because a node can only be opened once its parent's children have
    /// loaded. Each reopened node costs one children request — the price of restoring exactly what was left.
    /// </para>
    /// <para>
    /// <b>Pruning is deliberately narrow.</b> An entry is dropped only when its PARENT was opened here and the
    /// child did not come back: that folder is gone. An entry beneath a branch this restore never opened is
    /// left alone, because <i>not seen</i> and <i>not there</i> are different things — treating them alike
    /// would discard a user's state every time they collapsed something.
    /// </para>
    /// </remarks>
    public async Task RestoreAsync(IEnumerable<TreeNodeViewModel> roots)
    {
        _expanded.Clear();
        foreach (var entry in TreeExpansionStore.Load(_key))
        {
            _expanded.Add(entry);
        }

        if (_expanded.Count == 0)
        {
            return;
        }

        _restoring = true;
        var stale = new List<ExpandedNode>();
        try
        {
            var level = roots.Where(n => !n.IsSynthetic && !n.IsLauncher).ToList();
            while (level.Count > 0)
            {
                var next = new List<TreeNodeViewModel>();
                foreach (var node in level.Where(n => _expanded.Any(e => e.Id == n.Id)))
                {
                    await node.EnsureExpandedAsync();

                    // Everything remembered UNDER this node is now decidable: present means keep and descend,
                    // absent means the folder is gone.
                    var childIds = node.Children.Select(c => c.Id).ToHashSet();
                    stale.AddRange(_expanded.Where(e => e.ParentId == node.Id && !childIds.Contains(e.Id)));
                    next.AddRange(node.Children.Where(c => !c.IsSynthetic && !c.IsLauncher));
                }

                level = next;
            }
        }
        catch (Exception)
        {
            // A tree that opens flat is a small disappointment; a client that fails to start is not.
        }
        finally
        {
            _restoring = false;
        }

        if (stale.Count == 0)
        {
            return;
        }

        foreach (var entry in stale)
        {
            _expanded.Remove(entry);
        }

        TreeExpansionStore.Save(_key, _expanded);
    }
}
