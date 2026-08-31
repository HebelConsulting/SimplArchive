using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The Repositories, Intray, and Recycle-bin tabs each own a SEPARATE preview surface, so a preview shown on one
// tab never leaks onto another (the Intray preview was previously wired to the Repositories one). Pure VM guard.
// Since #517 the Intray's surface hangs off its own view-model, so the separation is structural rather than a
// convention this test has to police — it still asserts it, because "structural" is exactly what a refactor moves.
public class DesktopPreviewIsolationTests
{
    [Fact]
    public void Each_tab_owns_a_distinct_preview_that_does_not_share_state()
    {
        var vm = new MainWindowViewModel();

        Assert.NotSame(vm.Preview, vm.Intray.Preview);
        Assert.NotSame(vm.Preview, vm.RecycleBin.Preview);
        Assert.NotSame(vm.Intray.Preview, vm.RecycleBin.Preview);

        // Mutating one preview's state must not affect the others.
        vm.Preview.FindQuery = "repo";
        vm.Intray.Preview.FindQuery = "intray";
        Assert.Equal("repo", vm.Preview.FindQuery);
        Assert.Equal("intray", vm.Intray.Preview.FindQuery);
    }
}
