using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The tree's "you are here" mark (issue #686). It follows the folder the user has OPEN — not the row they have
// selected, which is what #696 shipped and what this supersedes.
//
// These are VM-level and construct the tree by hand ON PURPOSE. The --marked screenshot hook sets IsMarked
// synthetically on a node it finds BY NAME, so it proves the ring RENDERS and nothing at all about which node
// the product would choose; a check that only ran through that hook would be green with the logic deleted.
public class DesktopOpenFolderMarkTests
{
    private static TreeNodeViewModel Node(Guid id, string name) => new(id, name, hasSubfolders: false, loadChildren: null);

    [Fact]
    public void The_mark_lands_on_the_open_folder_wherever_it_sits_in_the_tree()
    {
        var deep = Node(Guid.NewGuid(), "Silvan Zingg");
        var branch = Node(Guid.NewGuid(), "Artists");
        branch.Children.Add(deep);
        var root = Node(Guid.NewGuid(), "Demo Repository");
        root.Children.Add(branch);

        // Not a child of the tree's SELECTED node, and several levels down: drilling in from the contents list
        // moves the open folder without moving the tree's selection, so the search cannot start from there.
        var marked = OpenFolderMark.Move([root], deep.Id);

        Assert.Same(deep, marked);
        Assert.True(deep.IsMarked);
        Assert.False(root.IsMarked);
        Assert.False(branch.IsMarked);
    }

    [Fact]
    public void Moving_the_mark_clears_the_one_before_it()
    {
        var first = Node(Guid.NewGuid(), "Invoices");
        var second = Node(Guid.NewGuid(), "Contracts");
        var root = Node(Guid.NewGuid(), "Demo Repository");
        root.Children.Add(first);
        root.Children.Add(second);
        first.IsMarked = true;

        OpenFolderMark.Move([root], second.Id);

        Assert.False(first.IsMarked);
        Assert.True(second.IsMarked);
    }

    // A stale mark is a claim about the wrong place (ADR 0559) — the same defect as a pane describing the
    // subject before last. So a folder the loaded tree does not hold clears the mark rather than keeping it.
    [Theory]
    [InlineData(true)]   // a folder the tree has not loaded — reached by "Go to", or under an unexpanded node
    [InlineData(false)]  // nothing open at all
    public void A_folder_the_tree_does_not_hold_is_not_marked_and_leaves_nothing_marked(bool openElsewhere)
    {
        var root = Node(Guid.NewGuid(), "Demo Repository");
        root.IsMarked = true;

        var marked = OpenFolderMark.Move([root], openElsewhere ? Guid.NewGuid() : null);

        Assert.Null(marked);
        Assert.False(root.IsMarked);
    }

    [Fact]
    public void The_open_folder_is_described_from_its_own_advertised_links()
    {
        var id = Guid.NewGuid();
        var links = new Dictionary<string, string> { ["self"] = "/api/documents/x", ["children"] = "/api/documents/x/children" };

        var row = OpenFolderMark.AsRow(id, "Artists", links, hasChildren: true);

        Assert.Equal(id, row.Id);
        Assert.Equal("Artists", row.Name);
        Assert.Same(links, row.Links);
        Assert.True(row.HasChildren);

        // A folder has no versions of its own to offer, whatever it contains.
        Assert.False(row.HasVersions);
    }

    // A repository root has no parent to be listed in, so its own details are reachable ONLY this way — which
    // is why the name has to survive being absent rather than throwing.
    [Fact]
    public void A_folder_with_no_name_yet_is_still_describable()
    {
        var row = OpenFolderMark.AsRow(Guid.NewGuid(), null, null, hasChildren: false);

        Assert.Equal(string.Empty, row.Name);
    }
}
