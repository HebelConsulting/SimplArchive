using Avalonia.Controls;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// A drag of the detail pane is a PEEK (ADR 0550, issue #413): it enlarges the pane now and leaves nothing behind.
//
// The web client leaked a dragged height through localStorage. The desktop leaked the same thing by a different
// route — the collapse toggle restored a remembered height, and SaveLayout wrote it to the settings file — so one
// drag became a lasting preference for every later selection and every later session. Same rule, same class of
// defect, two mechanisms; hence a guard on each side rather than trusting that one implies the other.
//
// View-model level: IndexHeight is what the XAML RowDefinition binds to, so GridLength.Auto here IS "fits its
// content" on screen.
[Collection("DesktopConfig")]
public class DesktopDetailPanePeekTests
{
    // Collapsing and re-expanding must return to fit-to-content, NOT to whatever the pane was last dragged to.
    // This is the desktop's version of "the peek does not outlive the thing it was for".
    [Fact]
    public void Expanding_the_detail_pane_fits_its_content_rather_than_a_dragged_height()
    {
        var vm = new MainWindowViewModel();

        // Stand in for a GridSplitter drag: the splitter writes an absolute height straight into the binding.
        vm.IndexHeight = new GridLength(400);

        vm.ToggleIndexCommand.Execute(null);   // collapse
        Assert.True(vm.IndexCollapsed);

        vm.ToggleIndexCommand.Execute(null);   // expand
        Assert.False(vm.IndexCollapsed);
        Assert.True(vm.IndexHeight.IsAuto,
            $"expanding restored a remembered height ({vm.IndexHeight}) instead of fitting the content");
    }

    // A peek must not reach the settings file — that is what stops it surviving a restart.
    [Fact]
    public void A_dragged_height_is_never_persisted()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"layout-{Guid.NewGuid():N}.json");
        LayoutSettingsStore.PathOverride = tmp;
        try
        {
            var vm = new MainWindowViewModel();
            vm.IndexHeight = new GridLength(400);
            vm.SaveLayout();

            Assert.Equal("Auto", LayoutSettingsStore.Load().IndexHeight);

            // And a fresh window off that file comes up fitted.
            Assert.True(new MainWindowViewModel().IndexHeight.IsAuto);
        }
        finally
        {
            LayoutSettingsStore.PathOverride = null;
            File.Delete(tmp);
        }
    }

    // Reset layout is an escape hatch for a drifted layout; it must land on the rule, not on an old proportion.
    [Fact]
    public void Reset_layout_leaves_the_detail_pane_fitting_its_content()
    {
        var vm = new MainWindowViewModel();
        vm.IndexHeight = new GridLength(400);

        vm.ResetLayoutCommand.Execute(null);

        Assert.False(vm.IndexCollapsed);
        Assert.True(vm.IndexHeight.IsAuto, $"reset left the pane at {vm.IndexHeight} instead of fitting its content");
    }
}
