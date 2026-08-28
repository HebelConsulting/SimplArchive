using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The contents list's column filters at the view-model level (the Tasks tab's pattern applied to the
// Repositories middle pane; DesktopTasksSortFilterTests is the sibling). The ListBox binds VisibleItems, a
// projection that follows Items through one CollectionChanged subscription — so these tests exercise exactly
// the seam every shell mutation site relies on. The rendered half (the scrollbar at the pane's edge, the
// synced header strip) lives in the `--list-scroll-test` harness, which needs a composed frame.
public class DesktopContentsFilterTests
{
    private static NodeViewModel Node(string name, string type = "Basic Entry", string owner = "Demo Admin", string[]? tags = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        HasChildren = false,
        HasVersions = true,
        DocumentType = type,
        CreatedBy = owner,
        Tags = tags ?? [],
    };

    [Fact]
    public void The_projection_follows_items_and_every_column_filter_narrows_it()
    {
        var vm = new MainWindowViewModel();
        vm.Items.Add(Node("Invoice March", type: "eMail", owner: "Anna", tags: ["urgent"]));
        vm.Items.Add(Node("Offer April"));
        vm.Items.Add(Node("Invoice May", tags: ["reviewed", "urgent"]));

        // No filter: the projection mirrors Items — including later additions, with no mutation site involved.
        Assert.Equal(3, vm.VisibleItems.Count);
        vm.Items.Add(Node("Contract June", owner: "Tom"));
        Assert.Equal(4, vm.VisibleItems.Count);

        vm.ContentsFilterName = "invoice";
        Assert.Equal(2, vm.VisibleItems.Count);

        vm.ContentsFilterTags = "urgent";
        Assert.Equal(2, vm.VisibleItems.Count);
        vm.ContentsFilterTags = "reviewed";
        Assert.Single(vm.VisibleItems);
        Assert.Equal("Invoice May", vm.VisibleItems[0].Name);

        vm.ContentsFilterName = string.Empty;
        vm.ContentsFilterTags = string.Empty;
        vm.ContentsFilterType = "email";   // case-insensitive, contained
        Assert.Single(vm.VisibleItems);

        vm.ContentsFilterType = string.Empty;
        vm.ContentsFilterOwner = "tom";
        Assert.Single(vm.VisibleItems);
        Assert.Equal("Contract June", vm.VisibleItems[0].Name);

        vm.ContentsFilterOwner = string.Empty;
        Assert.Equal(4, vm.VisibleItems.Count);
    }

    [Fact]
    public void A_folder_change_replacing_items_lands_in_the_projection()
    {
        // The shell replaces Items wholesale when another folder opens; the projection must follow without
        // anyone calling it — that is the CollectionChanged contract this feature stands on.
        var vm = new MainWindowViewModel();
        vm.Items.Add(Node("Old folder's row"));
        Assert.Single(vm.VisibleItems);

        vm.Items.Clear();
        vm.Items.Add(Node("New folder row A"));
        vm.Items.Add(Node("New folder row B"));
        Assert.Equal(2, vm.VisibleItems.Count);
        Assert.DoesNotContain(vm.VisibleItems, n => n.Name == "Old folder's row");
    }
}
