using System.Collections.ObjectModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// What the mask picker offers for a document that is already classified (ADR 0653 / #671).
//
// The reported symptom was on the web — a bare GUID where a mask name belongs. The desktop had the same cause
// and a worse effect: the picker fell through to its first entry, "(No mask)", so opening the index editor on a
// typed folder pre-selected "strip this folder's type" and a save the user believed changed one field would
// have carried that with it. No test saw either, because both are what a control does with a value it was
// never given — which is why this one asks the question directly rather than through the UI.
//
// The mask is ADDED to the picker, not substituted for it. Narrowing the list to the current mask was a first
// attempt and was wrong: it froze every folder and every extension-claimed document, and a web test caught it
// by failing to open a picker that no longer existed.
public class DesktopFixedMaskChoiceTests
{
    private static readonly Guid BasicEntryId = Guid.NewGuid();
    private static readonly Guid CalendarId = Guid.NewGuid();

    private static ObservableCollection<MaskChoiceViewModel> Catalogue() =>
    [
        new(null, "(No mask)"),
        new(BasicEntryId, "Basic Entry"),
    ];

    [Fact]
    public void A_mask_the_catalogue_does_not_carry_is_added_and_selected()
    {
        var choices = Catalogue();

        var selected = MaskChoices.Select(choices, new DocumentsClient.MaskInfo(CalendarId, "Calendar", 1));

        // Named, not a GUID and not "(No mask)" — and the alternatives survive, because "the catalogue does not
        // offer this" means the user may not CHOOSE it, not that they may not choose anything.
        Assert.Equal(CalendarId, selected.MaskId);
        Assert.Equal("Calendar", selected.Name);
        Assert.Equal(["(No mask)", "Calendar", "Basic Entry"], choices.Select(c => c.Name));
    }

    [Fact]
    public void An_ordinary_mask_leaves_the_catalogue_alone()
    {
        var choices = Catalogue();

        var selected = MaskChoices.Select(choices, new DocumentsClient.MaskInfo(BasicEntryId, "Basic Entry", 1));

        // The control: narrowing must happen ONLY for a mask that cannot be chosen, or the fix would quietly
        // make every document's mask unchangeable.
        Assert.Equal(BasicEntryId, selected.MaskId);
        Assert.Equal(["(No mask)", "Basic Entry"], choices.Select(c => c.Name));
    }

    [Fact]
    public void An_unclassified_document_still_selects_no_mask()
    {
        var choices = Catalogue();

        var selected = MaskChoices.Select(choices, new DocumentsClient.MaskInfo(null, null, null));

        Assert.Null(selected.MaskId);
        Assert.Equal(2, choices.Count);
    }
}
