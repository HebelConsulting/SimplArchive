using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop's external-link DETAIL dialog, from a bug found on the live kiosk: a link on a tenant that had
// opted into revealing URLs (issue #412) still reported "the URL is not shown", the Document row was blank, and
// the expiry disagreed with the row it was opened from by an hour.
//
// All three are view-model level, which is where these assertions belong — the failures were in what the dialog
// was TOLD, not in how Avalonia drew it.
public class DesktopExternalLinkDetailTests
{
    private static ExternalLinksClient.ExternalLinkInfo Link(
        string documentName = "", string? revealHref = null, DateTimeOffset? expiresAt = null) =>
        new(
            Id: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            DocumentName: documentName,
            Url: null,
            ExpiresAt: expiresAt ?? new DateTimeOffset(2026, 11, 11, 20, 40, 0, TimeSpan.Zero),
            MaxAccesses: null,
            AccessCount: 1,
            CreatedByName: "Demo Admin",
            CanExtend: false,
            Etag: "etag",
            RevokeHref: null,
            AvailabilityHref: null,
            ParentId: null,
            RevealUrlHref: revealHref);

    // The reported bug: the tenant HAS opted in (so the server advertises `reveal-url`), and the dialog must
    // offer the URL rather than the note that says it can never be shown.
    [Fact]
    public void A_tenant_that_reveals_urls_gets_a_button_not_the_never_shown_note()
    {
        var vm = new ExternalLinkDetailDialogViewModel(
            new SimplArchiveApiClient("t"), Link(revealHref: "/api/documents/x/external-links/y/url"));

        Assert.True(vm.CanReveal);
        Assert.True(vm.ShowRevealButton);
        Assert.False(vm.ShowUrlNote);
    }

    // …and the note is still right where the tenant has NOT opted in: a missing rel means "not available to
    // you, here, now" (ADR 0543), so the absence must read as an explanation rather than a broken button.
    [Fact]
    public void A_tenant_that_does_not_reveal_urls_keeps_the_note_and_gets_no_button()
    {
        var vm = new ExternalLinkDetailDialogViewModel(new SimplArchiveApiClient("t"), Link());

        Assert.False(vm.CanReveal);
        Assert.False(vm.ShowRevealButton);
        Assert.True(vm.ShowUrlNote);
    }

    // The per-document listing sends an empty documentName — the caller is already sitting on that document —
    // so a dialog that trusted the row rendered a blank "Document". The caller's name is used instead.
    [Fact]
    public void The_document_name_comes_from_the_caller_when_the_row_has_none()
    {
        var vm = new ExternalLinkDetailDialogViewModel(
            new SimplArchiveApiClient("t"), Link(documentName: ""), "MyCountry Telekom — service agreement");

        Assert.Equal("MyCountry Telekom — service agreement", vm.DocumentName);
    }

    // The cross-document list DOES carry the name, and there the caller has none to offer — the row wins.
    [Fact]
    public void The_row_supplies_the_name_when_the_caller_has_none()
    {
        var vm = new ExternalLinkDetailDialogViewModel(
            new SimplArchiveApiClient("t"), Link(documentName: "From the row"), documentName: null);

        Assert.Equal("From the row", vm.DocumentName);
    }

    // One link showed two expiry times an hour apart: the row formatted the raw UTC value while the detail
    // called ToLocalTime(). Both now read the same property, so they cannot drift — asserted against the local
    // conversion rather than a fixed string, since the test machine's zone is not the subject.
    [Fact]
    public void The_row_and_the_detail_agree_on_the_expiry()
    {
        var expiry = new DateTimeOffset(2026, 11, 11, 20, 40, 0, TimeSpan.Zero);
        var link = Link(expiresAt: expiry);
        var vm = new ExternalLinkDetailDialogViewModel(new SimplArchiveApiClient("t"), link);

        Assert.Equal(link.ExpiresLocal, vm.Expires);
        Assert.Equal(expiry.ToLocalTime().ToString("g"), link.ExpiresLocal);
    }
}
