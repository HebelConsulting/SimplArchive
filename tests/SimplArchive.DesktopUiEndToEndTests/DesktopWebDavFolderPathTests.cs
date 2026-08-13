using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The Repositories ribbon's WebDAV button opens the folder the user is looking at, because the mounted volume
// IS the tree-pane (ADR 0509) — so "where am I" and "which mount folder" are the same question.
public class DesktopWebDavFolderPathTests
{
    [Fact]
    public void The_root_crumb_alone_means_open_the_whole_archive()
    {
        var vm = new MainWindowViewModel();
        vm.Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Repositories", FolderId = null, ShowSeparator = false });

        // Empty = the mount root. Deliberately not "do nothing": a button that does nothing when pressed reads
        // as broken, which is exactly how this one was reported.
        Assert.Equal(string.Empty, vm.WebDavFolderPath());
    }

    [Fact]
    public void A_selected_folder_becomes_its_path_inside_the_mount()
    {
        var vm = new MainWindowViewModel();
        vm.Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Repositories", FolderId = null, ShowSeparator = false });
        vm.Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Demo Repository", FolderId = Guid.NewGuid(), ShowSeparator = true });
        vm.Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Invoices", FolderId = Guid.NewGuid(), ShowSeparator = true });

        // "Repositories" is a UI label, not a folder — the WebDAV root lists the repositories themselves.
        Assert.Equal("Demo Repository/Invoices", vm.WebDavFolderPath());
    }

    [Fact]
    public void A_name_carrying_a_slash_falls_back_to_the_root_rather_than_addressing_the_wrong_folder()
    {
        var vm = new MainWindowViewModel();
        vm.Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Repositories", FolderId = null, ShowSeparator = false });
        vm.Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Demo Repository", FolderId = Guid.NewGuid(), ShowSeparator = true });
        vm.Breadcrumbs.Add(new BreadcrumbViewModel { Name = "2026/Q1", FolderId = Guid.NewGuid(), ShowSeparator = true });

        // Joining that in would silently address "2026" then "Q1" — two folders that may well exist and are not
        // the one selected. Opening the archive root is wrong-but-visible; the alternative is wrong-but-silent.
        Assert.Equal(string.Empty, vm.WebDavFolderPath());
    }
}
