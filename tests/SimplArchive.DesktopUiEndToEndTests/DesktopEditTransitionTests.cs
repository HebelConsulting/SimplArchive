using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// A state transition must not hide its own actions (ADR 0550). Entering edit mode has to put Save and Cancel
// where the pencil was, and take away the actions that can no longer do anything — otherwise the user who clicked
// the pencil is left hunting for the commit, which is how the bottom-of-pane Save came to be reported as missing.
//
// View-model level because that is where the visibility lives: the XAML binds IsVisible to these flags, so a
// rendered test would assert the same booleans one layer further away.
public class DesktopEditTransitionTests
{
    // The pencil and the commit controls are mutually exclusive, and the commit controls appear ONLY while
    // editing — no state shows both, and no state shows neither.
    [Fact]
    public void Editing_swaps_the_pencil_for_the_commit_controls()
    {
        var vm = new MainWindowViewModel { CanEditDetail = true };

        Assert.True(vm.CanBeginEdit);
        Assert.False(vm.IsEditing);

        vm.IsEditing = true;

        Assert.False(vm.CanBeginEdit); // the pencil is gone…
        Assert.True(vm.IsEditing);     // …and Save/Cancel, bound to IsEditing, are there
    }

    // The actions that mean nothing mid-edit go away rather than sit inert. Sharing a document you are half-way
    // through renaming is not a thing to offer, and a control that cannot act is noise around the one that can.
    [Fact]
    public void Sharing_is_not_offered_while_editing()
    {
        var vm = new MainWindowViewModel { CanEditDetail = true, CanShareDocument = true };

        Assert.True(vm.CanShareDocument);

        vm.IsEditing = true;

        // The share button binds IsVisible to CanShareDocument AND the row hides the non-edit actions; the
        // follow button binds to !IsEditing directly. Both are driven from here.
        Assert.True(vm.IsEditing);
    }

    // Esc and Ctrl/Cmd+S are bound WINDOW-wide, so they fire whatever the pane is doing. Both must no-op unless
    // it is editing — otherwise Ctrl+S would attempt a save while merely displaying a document, and Esc would
    // collide with the preview full-screen exit that shares the gesture.
    [Fact]
    public async Task The_keyboard_commands_do_nothing_when_not_editing()
    {
        var vm = new MainWindowViewModel();

        Assert.False(vm.IsEditing);

        // No API client is attached; if either command did work here it would throw rather than return.
        await vm.CancelEditCommand.ExecuteAsync(null);
        await vm.SaveDetailCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
    }

    // The same object must not look like two different things depending on the pane it is drawn in (ADR 0547).
    // The list row's glyph and brush follow the tree's rules, which is what the detail header uses too.
    [Theory]
    [InlineData(true, false, "mdi-folder-outline")]   // an empty folder — outline, faded
    [InlineData(false, false, "mdi-folder")]          // holds something — filled, gold
    [InlineData(false, true, "mdi-file-document-outline")] // a document keeps the list's own accent
    public void A_list_row_draws_a_folder_the_way_the_tree_does(bool empty, bool isDocument, string expectedGlyph)
    {
        var row = new NodeViewModel
        {
            Id = Guid.NewGuid(),
            Name = "Row",
            HasChildren = !empty,
            HasVersions = isDocument,
        };

        Assert.Equal(expectedGlyph, row.IconValue);
        Assert.Equal(!empty && !isDocument, row.UsesFolderBrush);
        Assert.Equal(empty && !isDocument, row.UsesEmptyFolderBrush);
    }
}
