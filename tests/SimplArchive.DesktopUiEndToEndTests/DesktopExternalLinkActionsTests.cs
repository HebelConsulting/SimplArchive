using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The two row actions the cross-document links dialog gained (ADR 0546): "Go to", which hands the document back to
// the workbench, and "Show", which opens the link's own details with renewal as the only live control.
//
// View-model level, deliberately: both actions are decisions about WHICH surface does what — a dialog cannot move
// the tree pane, and a row-sized button cannot ask two questions — and that is exactly what a rendered test would
// not pin down.
public class DesktopExternalLinkActionsTests
{
    [Fact]
    public void Go_to_is_offered_only_in_the_cross_document_list()
    {
        // The per-document dialog is already sitting on the document, so "Go to" there would select what the
        // reader is looking at.
        Assert.False(Dialog(crossDocument: false).ShowGoTo);
        Assert.True(Dialog(crossDocument: true).ShowGoTo);
    }

    [Fact]
    public void Go_to_hands_the_document_and_its_folder_to_the_host()
    {
        var dialog = Dialog(crossDocument: true);
        var documentId = Guid.NewGuid();
        var parentId = Guid.NewGuid();

        (Guid Document, Guid? Parent)? navigated = null;
        var closed = false;
        dialog.GoToDocument = (d, p) => navigated = (d, p);
        dialog.RequestClose = () => closed = true;

        dialog.GoToCommand.Execute(Link(documentId, parentId));

        // The PARENT is what the workbench needs — it opens that folder and selects the document inside it.
        Assert.Equal((documentId, parentId), navigated);

        // Closing is the host's job, invoked from its own callback rather than from the command, so the dialog
        // does not dismiss itself before the caller has read what it handed over.
        Assert.False(closed);
    }

    // Renewal is offered only when the link is near enough to its end AND the server advertised the rel — a
    // missing rel means "not available to you, here, now", so the control goes rather than leading to a refusal.
    [Theory]
    [InlineData(true, "https://example.test/availability", true)]
    [InlineData(false, "https://example.test/availability", false)]
    [InlineData(true, null, false)]
    public void Renewal_is_offered_only_when_it_is_both_due_and_permitted(bool canExtend, string? href, bool expected)
    {
        var link = Link(Guid.NewGuid(), Guid.NewGuid()) with { CanExtend = canExtend, AvailabilityHref = href };

        Assert.Equal(expected, new ExternalLinkDetailDialogViewModel(null!, link).CanRenew);
    }

    // The cap starts at what the link already has, so applying without touching it does not silently change it —
    // the field is a current value to adjust, not a blank to fill.
    [Fact]
    public void The_detail_dialog_starts_from_the_links_current_cap()
    {
        var capped = new ExternalLinkDetailDialogViewModel(null!, Link(Guid.NewGuid(), null) with { MaxAccesses = 5 });
        Assert.Equal(5, capped.MaxAccesses);

        // Unlimited stays null rather than becoming 0: null is what the server takes, so there is no "0 means
        // unlimited" convention for anyone to learn or mistype.
        var unlimited = new ExternalLinkDetailDialogViewModel(null!, Link(Guid.NewGuid(), null) with { MaxAccesses = null });
        Assert.Null(unlimited.MaxAccesses);
        Assert.Contains("/", unlimited.Accesses, StringComparison.Ordinal);
    }

    private static ExternalLinksDialogViewModel Dialog(bool crossDocument) =>
        new(null!, "https://example.test/links", "Links", crossDocument);

    private static SimplArchiveApiClient.ExternalLinkInfo Link(Guid documentId, Guid? parentId) =>
        new(Guid.NewGuid(), documentId, "Doc", null, DateTimeOffset.UtcNow.AddDays(10),
            MaxAccesses: null, AccessCount: 0, CreatedByName: "Demo Admin", CanExtend: true, Etag: "etag",
            RevokeHref: null, AvailabilityHref: null, ParentId: parentId);
}
