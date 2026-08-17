using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The Repositories, Intray, and Recycle-bin tabs each own a SEPARATE preview surface, so a preview shown on one
// tab never leaks onto another (the Intray preview was previously wired to the Repositories one). Pure VM guard.
public class DesktopPreviewIsolationTests
{
    [Fact]
    public void Each_tab_owns_a_distinct_preview_that_does_not_share_state()
    {
        var vm = new MainWindowViewModel();

        Assert.NotSame(vm.Preview, vm.IntrayPreview);
        Assert.NotSame(vm.Preview, vm.RecycleBin.Preview);
        Assert.NotSame(vm.IntrayPreview, vm.RecycleBin.Preview);

        // Mutating one preview's state must not affect the others.
        vm.Preview.FindQuery = "repo";
        vm.IntrayPreview.FindQuery = "intray";
        Assert.Equal("repo", vm.Preview.FindQuery);
        Assert.Equal("intray", vm.IntrayPreview.FindQuery);
    }
}
