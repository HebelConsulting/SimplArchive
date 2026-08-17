using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The client half of the intray page operations (#487, ADR 0575): the reordering arithmetic both dialogs share,
// and the gating that decides whether their buttons are offered at all.
//
// View-model / helper level rather than rendered, for the reason the edit-transition tests give: this is where
// the behaviour lives. The XAML binds IsEnabled to these flags and the dialogs delegate their moves to
// ListOrder, so a rendered test would assert the same values one layer further away — and could not run in the
// Chrome-free desktop suite at all.
public class DesktopIntrayPageOperationsTests
{
    // Moving returns where the entry ENDED UP, because the selection has to follow the entry rather than the
    // slot. Without that, a user moving one page twice moves a different page the second time.
    [Fact]
    public void Moving_an_entry_returns_its_new_index()
    {
        var pages = new List<int> { 1, 2, 3, 4 };

        Assert.Equal(0, ListOrder.Move(pages, 1, -1));
        Assert.Equal([2, 1, 3, 4], pages);

        Assert.Equal(3, ListOrder.Move(pages, 2, +1));
        Assert.Equal([2, 1, 4, 3], pages);
    }

    // A move off either end is a no-op that keeps the selection where it was — the first page cannot go
    // earlier, and clicking again must not silently wrap it to the back.
    [Theory]
    [InlineData(0, -1)]
    [InlineData(2, +1)]
    [InlineData(-1, +1)]  // nothing selected
    [InlineData(9, -1)]   // out of range
    public void A_move_off_the_end_changes_nothing(int index, int delta)
    {
        var pages = new List<int> { 1, 2, 3 };

        Assert.Equal(index, ListOrder.Move(pages, index, delta));
        Assert.Equal([1, 2, 3], pages);
    }

    // ADR 0554: an action that cannot succeed is not advertised. With no pages resource — a .docx, a one-page
    // PDF, or nothing selected — neither split nor sort is offered.
    [Fact]
    public void Without_a_pages_resource_no_page_action_is_offered()
    {
        var actions = new IntrayItemActionsViewModel();

        Assert.False(actions.CanSplit);
        Assert.False(actions.CanSort);
    }

    // Each button follows its OWN rel: a server that offered only one of them must not light up both.
    [Theory]
    [InlineData("api/intray/a.pdf/pages/split", null, true, false)]
    [InlineData(null, "api/intray/a.pdf/pages/order", false, true)]
    [InlineData("api/intray/a.pdf/pages/split", "api/intray/a.pdf/pages/order", true, true)]
    public void Each_page_action_follows_its_own_rel(string? split, string? sort, bool canSplit, bool canSort)
    {
        var actions = new IntrayItemActionsViewModel
        {
            Pages = new IntrayApi.PagesInfo("pdf", 3, split, sort),
        };

        Assert.Equal(canSplit, actions.CanSplit);
        Assert.Equal(canSort, actions.CanSort);
    }

    // Cutting at separator sheets is its own rel too (#492), and it is the one whose absence is easiest to get
    // wrong: the server withholds it for a one-page file and for a signed document, and a client that inferred
    // it from "this is a multi-page PDF" would offer a button that 400s on the second of those.
    [Theory]
    [InlineData("api/intray/a.pdf/patch-codes", true)]
    [InlineData(null, false)]
    public void Cutting_at_separator_sheets_follows_its_own_rel(string? patchCodesHref, bool canCut)
    {
        var actions = new IntrayItemActionsViewModel
        {
            Pages = new IntrayApi.PagesInfo("pdf", 6, null, null, PatchCodesHref: patchCodesHref),
        };

        Assert.Equal(canCut, actions.CanCutAtPatchCodes);
    }

    // Join needs BOTH a multiple selection and the collection's advertised address. The selection alone is not
    // enough: a server that stopped offering the join must take the button with it (ADR 0543).
    [Theory]
    [InlineData(0, "api/intray/from-items", false)]
    [InlineData(1, "api/intray/from-items", false)]
    [InlineData(2, "api/intray/from-items", true)]
    [InlineData(2, null, false)]
    public void Join_needs_two_items_and_an_advertised_address(int selected, string? joinHref, bool canJoin)
    {
        var actions = new IntrayItemActionsViewModel { SelectedCount = selected, JoinHref = joinHref };

        Assert.Equal(canJoin, actions.CanJoin);
    }

    // The gating is CLEARED before the newly selected row is asked about, so the buttons never describe the
    // previous selection while a request is in flight (ADR 0559). Passing null is that clear.
    [Fact]
    public async Task Changing_the_selection_clears_the_previous_rows_actions()
    {
        var actions = new IntrayItemActionsViewModel
        {
            Pages = new IntrayApi.PagesInfo("pdf", 3, "api/intray/a.pdf/pages/split", "api/intray/a.pdf/pages/order"),
        };

        Assert.True(actions.CanSplit);

        await actions.LoadPagesAsync(null);

        Assert.Null(actions.Pages);
        Assert.False(actions.CanSplit);
        Assert.False(actions.CanSort);
        Assert.False(actions.CanCutAtPatchCodes);
    }
}
